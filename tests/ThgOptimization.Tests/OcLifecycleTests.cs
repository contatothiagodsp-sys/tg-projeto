global using FanEngine = ThgOptimization.FanControlEngine;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ThgOptimization.Core.Oc;

namespace ThgOptimization.Tests;

public class OcGateClosedTests
{
    private sealed class CountingNvml : INvmlOcTransport
    {
        public int Reads, Writes;
        public int GetGpcOffset(out int mhz) { Reads++; mhz = 0; return 0; }
        public int SetGpcOffset(int mhz) { Writes++; return 0; }
        public double? TempC() => 50;
    }

    [Fact]
    public void Real_provider_touches_nothing_while_the_gate_is_closed()
    {
        var io = new CountingNvml();
        var p = new NvidiaGpuProvider(io, isSimulated: false);
        Assert.Equal(OcStatus.BlockedByGate, p.Apply(new OcRequest(OcVendor.NvidiaGpu, 100)).Status);
        Assert.Equal(OcStatus.BlockedByGate, p.Restore().Status);
        Assert.Equal(0, io.Reads);
        Assert.Equal(0, io.Writes);
    }

    [Fact]
    public void Simulated_provider_keeps_working()
    {
        var p = new NvidiaGpuProvider(new CountingNvml(), isSimulated: true);
        Assert.NotEqual(OcStatus.BlockedByGate, p.Apply(new OcRequest(OcVendor.NvidiaGpu, 100)).Status);
    }

    [Fact]
    public void Pawnio_transport_does_not_open_the_driver_while_closed()
    {
        using var io = new PawnIoTransport();
        Assert.False(((ISmuTransport)io).Read(0x1000, out _));
        Assert.Contains("desligado", io.LastError);
        Assert.False(((ISmuTransport)io).Write(0x1000, 1));
        Assert.False(((IMsrTransport)io).Read(0x150, out _));
        Assert.False(((IMsrTransport)io).Write(0x150, 1));
    }

    [Fact]
    public void Nvml_write_does_not_reach_the_driver_while_closed()
    {
        Assert.Equal(4, new NvmlOcTransport(IntPtr.Zero).SetGpcOffset(100)); // IntPtr.Zero chegaria na NVML como erro de outro tipo
    }

    [Fact]
    public void Adlx_write_is_refused_before_loading_anything()
    {
        using var io = new AmdAdlxOverdriveTransport();
        Assert.False(io.TrySetOffsetMhz(100));
        Assert.Contains("desligado", io.LastError);
    }

    [Fact]
    public void Scope_ends_with_dispose()
    {
        using (OcGate.AllowInTests()) Assert.Null(OcGate.Block(isSimulated: false));
        Assert.NotNull(OcGate.Block(isSimulated: false));
    }

    [Fact]
    public void Criteria_to_open_are_written_down() => Assert.NotEmpty(OcGate.PendingCriteria);

    [Fact]
    public void Pawnio_dispose_is_repeatable_and_later_calls_fail_quietly()
    {
        using var scope = OcGate.AllowInTests();
        var io = new PawnIoTransport();
        io.Dispose();
        io.Dispose();
        Assert.False(((ISmuTransport)io).Write(0x1000, 1));
        Assert.False(((IMsrTransport)io).Read(0x150, out _));
    }
}

public class ProviderRestoreTests
{
    private sealed class Fake : IAmdOverdriveTransport
    {
        public int Mhz, Sets, Reconnects;
        public bool RefuseSets, ReadsFail, Stuck;
        public bool TryGetOffsetMhz(out int mhz) { mhz = Mhz; return !ReadsFail; }
        public bool TrySetOffsetMhz(int mhz) { Sets++; if (RefuseSets) return false; if (!Stuck) Mhz = mhz; return true; }
        public double? TempC() => 50;
        public void Reconnect() => Reconnects++;
    }

    [Fact]
    public void Restore_without_an_adjustment_never_writes_over_the_user_setting()
    {
        var io = new Fake { Mhz = 123 };
        var r = new AmdGpuProvider(io, true).Restore();
        Assert.True(r.Ok);
        Assert.Equal(0, io.Sets);
        Assert.Equal(123, io.Mhz);
    }

    [Fact]
    public void Restore_is_not_reported_without_reading_the_value_back()
    {
        var io = new Fake();
        var p = new AmdGpuProvider(io, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.AmdGpu, 200)).Ok);
        io.ReadsFail = true;
        Assert.Equal(OcStatus.Failed, p.Restore().Status);
    }

    [Fact]
    public void Restore_with_a_different_value_read_back_fails_and_can_be_retried()
    {
        var io = new Fake();
        var p = new AmdGpuProvider(io, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.AmdGpu, 200)).Ok);
        io.Stuck = true;
        Assert.Equal(OcStatus.Failed, p.Restore().Status);
        io.Stuck = false;
        Assert.True(p.Restore().Ok);
        Assert.Equal(0, io.Mhz);
    }

    [Fact]
    public void Failed_apply_says_when_the_value_could_not_be_restored()
    {
        var io = new Fake { Stuck = true, Mhz = 10 };
        var p = new AmdGpuProvider(io, true);
        var r = p.Apply(new OcRequest(OcVendor.AmdGpu, 200));
        Assert.Equal(OcStatus.Failed, r.Status);
        Assert.Equal(10, io.Mhz);
    }

    [Fact]
    public void Failed_apply_reports_a_refused_undo()
    {
        var io = new Fake { RefuseSets = true };
        var r = new AmdGpuProvider(io, true).Apply(new OcRequest(OcVendor.AmdGpu, 200));
        Assert.Contains("Também não foi possível voltar", r.Message);
    }
}

public class WatchdogLifecycleTests
{
    private sealed class SlowProvider : IOverclockProvider
    {
        public OcVendor Vendor => OcVendor.AmdGpu;
        public bool IsSimulated => true;
        public int Restores, Reconnects, FailuresLeft;
        public ManualResetEventSlim? Hold;
        public readonly ManualResetEventSlim Entered = new(false);
        public OcTelemetry ReadTelemetry() => new(50, null);
        public OcResult Apply(OcRequest request) => new(OcStatus.Applied, "");
        public OcResult Restore()
        {
            Entered.Set();
            Hold?.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref Restores);
            return Interlocked.Decrement(ref FailuresLeft) >= 0 ? new(OcStatus.Failed, "driver voltando") : new(OcStatus.Restored, "ok");
        }
        public void Reconnect() => Interlocked.Increment(ref Reconnects);
    }

    private static OcRollbackWatchdog Dog(SlowProvider p, HardwareErrorMonitor m)
    {
        var dog = new OcRollbackWatchdog(p, m) { RestoreRetryDelay = TimeSpan.Zero };
        dog.Arm(DateTime.Now, TimeSpan.FromHours(1));
        return dog;
    }

    [Fact]
    public void Hardware_event_returns_at_once_even_with_the_driver_stuck()
    {
        using var m = new HardwareErrorMonitor(subscribe: false);
        var p = new SlowProvider { Hold = new ManualResetEventSlim(false) };
        using var dog = Dog(p, m);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        m.Report("Display 4101");
        Assert.True(clock.ElapsedMilliseconds < 1000);
        Assert.False(dog.Armed);
        Assert.True(p.Entered.Wait(TimeSpan.FromSeconds(5)));
        p.Hold.Set();
        Assert.True(dog.WaitForRollback(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Lock_is_free_while_the_driver_is_stuck()
    {
        using var m = new HardwareErrorMonitor(subscribe: false);
        var p = new SlowProvider { Hold = new ManualResetEventSlim(false) };
        using var dog = Dog(p, m);
        m.Report("Display 4101");
        Assert.True(p.Entered.Wait(TimeSpan.FromSeconds(5)));
        var call = Task.Run(() => { dog.Confirm(); dog.Disarm(); return dog.Tick(DateTime.Now); });
        // Com o Sleep dentro da trava, isto esperaria a restauração.
        Assert.Same(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(2))));
        p.Hold.Set();
    }

    [Fact]
    public void Many_events_queue_a_single_rollback()
    {
        using var m = new HardwareErrorMonitor(subscribe: false);
        var p = new SlowProvider();
        using var dog = Dog(p, m);
        Parallel.For(0, 50, _ => m.Report("WHEA 18"));
        Assert.True(dog.WaitForRollback(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, p.Restores);
    }

    [Fact]
    public void Tdr_retries_reopening_the_backend_and_reports_success()
    {
        using var m = new HardwareErrorMonitor(subscribe: false);
        var p = new SlowProvider { FailuresLeft = 2 };
        using var dog = Dog(p, m);
        m.Report("Display 4101");
        Assert.True(dog.WaitForRollback(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, p.Restores);
        Assert.Equal(2, p.Reconnects);
        Assert.True(dog.LastRollback!.Restored);
        Assert.Equal(3, dog.LastRollback.Attempts);
    }

    [Fact]
    public void Failure_is_reported_and_never_as_success()
    {
        using var m = new HardwareErrorMonitor(subscribe: false);
        var p = new SlowProvider { FailuresLeft = 100 };
        using var dog = Dog(p, m);
        string? reason = null;
        dog.RolledBack += why => reason = why;
        m.Report("Display 4101");
        Assert.True(dog.WaitForRollback(TimeSpan.FromSeconds(5)));
        Assert.Equal(OcRollbackWatchdog.RestoreAttempts, p.Restores);
        Assert.False(dog.LastRollback!.Restored);
        Assert.Contains("reinicie o PC", reason);
    }

    [Fact]
    public void Dispose_removes_the_subscription()
    {
        using var m = new HardwareErrorMonitor(subscribe: false);
        var p = new SlowProvider();
        var dog = Dog(p, m);
        dog.Dispose();
        dog.Dispose();
        m.Report("Display 4101");
        Assert.Equal(0, p.Restores);
    }

    [Fact]
    public void Exception_in_a_rollback_subscriber_does_not_escape()
    {
        using var m = new HardwareErrorMonitor(subscribe: false);
        var p = new SlowProvider();
        using var dog = Dog(p, m);
        dog.RolledBack += _ => throw new InvalidOperationException("tela");
        m.Report("Display 4101");
        Assert.True(dog.WaitForRollback(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, p.Restores);
    }

    [Fact]
    public async Task Stop_waits_for_the_reading_in_progress_and_nothing_runs_after()
    {
        var p = new SlowProvider();
        var dog = new OcRollbackWatchdog(p, () => 0) { MonitorInterval = TimeSpan.FromMilliseconds(5) };
        dog.StartBackgroundMonitor(TimeSpan.FromHours(1));
        await Task.Delay(30);
        await dog.StopBackgroundMonitorAsync();
        Assert.False(dog.Armed);
        Assert.Equal(0, p.Restores);
        dog.Dispose();
    }

    [Fact]
    public async Task Monitor_that_cannot_read_rolls_back()
    {
        var p = new SlowProvider();
        int reads = 0;
        // A primeira leitura é a do Arm; depois disso o registro de eventos para de responder.
        using var dog = new OcRollbackWatchdog(p, () => Interlocked.Increment(ref reads) == 1 ? 0 : throw new InvalidOperationException("sem registro"))
        {
            MonitorInterval = TimeSpan.FromMilliseconds(5), RestoreRetryDelay = TimeSpan.Zero,
        };
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        dog.RolledBack += why => done.TrySetResult(why);
        dog.StartBackgroundMonitor(TimeSpan.FromHours(1));
        Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Contains("Falha ao vigiar", await done.Task);
        Assert.Equal(1, p.Restores);
        Assert.False(dog.Armed);
    }

    [Fact]
    public void Monitor_dispose_unsubscribes_and_ignores_late_reports()
    {
        var m = new HardwareErrorMonitor(subscribe: false);
        int seen = 0;
        m.ErrorDetected += _ => seen++;
        m.Report("a");
        m.Dispose();
        m.Dispose();
        m.Report("b");
        Assert.Equal(1, seen);
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public void Monitor_subscriber_with_error_does_not_stop_the_others()
    {
        using var m = new HardwareErrorMonitor(subscribe: false);
        int seen = 0;
        m.ErrorDetected += _ => throw new InvalidOperationException();
        m.ErrorDetected += _ => seen++;
        m.Report("a");
        Assert.Equal(1, seen);
    }
}

public class HardwareMutexLifecycleTests
{
    private static HardwareMutex New() => new(@"Local\thg-teste-" + Guid.NewGuid().ToString("N"));

    private static T OnThread<T>(Func<T> body)
    {
        T result = default!;
        var t = new Thread(() => result = body());
        t.Start();
        t.Join();
        return result;
    }

    [Fact]
    public void Times_out_while_another_thread_holds_it()
    {
        using var m = New();
        Assert.True(m.TryEnter(TimeSpan.FromSeconds(1)));
        Assert.False(OnThread(() => m.TryEnter(TimeSpan.FromMilliseconds(20))));
        m.Exit();
        Assert.True(OnThread(() => { bool ok = m.TryEnter(TimeSpan.FromSeconds(1)); m.Exit(); return ok; }));
    }

    [Fact]
    public void Exit_from_another_thread_neither_throws_nor_releases()
    {
        using var m = New();
        Assert.True(m.TryEnter(TimeSpan.FromSeconds(1)));
        OnThread(() => { m.Exit(); return 0; });
        Assert.False(OnThread(() => m.TryEnter(TimeSpan.FromMilliseconds(20))));
        m.Exit();
    }

    [Fact]
    public void Double_exit_does_not_throw()
    {
        using var m = New();
        Assert.True(m.TryEnter(TimeSpan.FromSeconds(1)));
        m.Exit();
        m.Exit();
    }

    [Fact]
    public void Exit_without_enter_does_not_throw()
    {
        using var m = New();
        m.Exit();
    }

    [Fact]
    public void Abandoned_mutex_is_acquired()
    {
        using var m = New();
        OnThread(() => m.TryEnter(TimeSpan.FromSeconds(1))); // a thread termina sem soltar
        Assert.True(m.TryEnter(TimeSpan.FromSeconds(1)));
        m.Exit();
    }

    [Fact]
    public void Dispose_releases_what_this_thread_holds()
    {
        string name = @"Local\thg-teste-" + Guid.NewGuid().ToString("N");
        var m = new HardwareMutex(name);
        Assert.True(m.TryEnter(TimeSpan.FromSeconds(1)));
        Assert.True(m.TryEnter(TimeSpan.FromSeconds(1)));
        m.Dispose();
        m.Dispose();
        using var other = new HardwareMutex(name);
        Assert.True(OnThread(() => { bool ok = other.TryEnter(TimeSpan.FromSeconds(1)); other.Exit(); return ok; }));
    }

    [Fact]
    public void After_dispose_it_refuses_quietly()
    {
        var m = New();
        m.Dispose();
        Assert.False(m.TryEnter(TimeSpan.FromMilliseconds(1)));
        m.Exit();
    }

    /// <summary>A caixa de mensagens e o transporte abrem o mesmo mutex nomeado com objetos diferentes, na mesma thread.</summary>
    [Fact]
    public void Two_objects_with_the_same_name_are_recursive_on_the_same_thread()
    {
        string name = @"Local\thg-teste-" + Guid.NewGuid().ToString("N");
        using var mailbox = new HardwareMutex(name);
        using var transport = new HardwareMutex(name);
        Assert.True(mailbox.TryEnter(TimeSpan.FromSeconds(1)));
        Assert.True(transport.TryEnter(TimeSpan.FromMilliseconds(50)));
        transport.Exit();
        Assert.False(OnThread(() => mailbox.TryEnter(TimeSpan.FromMilliseconds(20)))); // a conversa inteira continua protegida
        mailbox.Exit();
    }
}

/// <summary>ADLX falso em memória nativa, com os mesmos slots dos headers do SDK, para contar cada referência.</summary>
public sealed unsafe class AdlxLifecycleTests : IDisposable
{
    private const int KindSystem = 0, KindServices = 1, KindPerf = 2, KindList = 3, KindGpu = 4, KindGeneric = 5, KindManual = 6, KindMetrics = 7, Kinds = 8;
    private static readonly int[] Live = new int[Kinds];
    private static readonly nint[] Objects = new nint[Kinds];
    private static int _terminates, _inits, _setResult, _maxFreq = 2500, _qiResult, _getResult;
    private static bool _qiFillsPointer;
    private readonly List<nint> _memory = new();
    private readonly IDisposable _scope = OcGate.AllowInTests();

    private static int KindOf(nint obj) => (int)((long*)obj)[1];

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int Acquire(nint o) => ++Live[KindOf(o)];
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int Release(nint o) => --Live[KindOf(o)];
    private static int Give(int kind, nint* output) { *output = Objects[kind]; Live[kind]++; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int GetGpus(nint s, nint* o) => Give(KindList, o);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int GetTuning(nint s, nint* o) => Give(KindServices, o);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int GetPerf(nint s, nint* o) => Give(KindPerf, o);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static uint ListSize(nint l) => 1;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static uint ListBegin(nint l) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int ListAt(nint l, uint i, nint* o) => Give(KindGpu, o);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int GpuName(nint g, byte** name) { *name = null; return 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int IsManualSupported(nint s, nint g, byte* v) { *v = 1; return 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int GetManual(nint s, nint g, nint* o) => Give(KindGeneric, o);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryManual(nint obj, char* iid, nint* o)
    {
        if (_qiResult != 0 && !_qiFillsPointer) return _qiResult;
        Give(KindManual, o);
        return _qiResult;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int MaxRange(nint m, int* r) { r[0] = 500; r[1] = 3500; r[2] = 1; return 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int GetMax(nint m, int* v) { *v = _maxFreq; return _getResult; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int SetMax(nint m, int v) { if (_setResult == 0) _maxFreq = v; return _setResult; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int MaxDefault(nint m, int* v) { *v = 2500; return 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int GetMetrics(nint p, nint g, nint* o) => Give(KindMetrics, o);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int Temperature(nint m, double* v) { *v = 55; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int QueryVersion(ulong* v) { *v = 1; return 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int Init(ulong version, nint* system) { _inits++; *system = Objects[KindSystem]; return 0; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Terminate()
    {
        _terminates++;
        // Terminar com objetos vivos é o ADLX_ORPHAN_OBJECTS: tudo tem que ter sido liberado antes.
        for (int k = 1; k < Kinds; k++) if (Live[k] != 0) return 11;
        return 0;
    }

    private nint Build(int kind, params (int Slot, nint Fn)[] slots)
    {
        var vtable = (nint*)NativeMemory.AllocZeroed(32, (nuint)sizeof(nint));
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Acquire;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Release;
        foreach (var (slot, fn) in slots) vtable[slot] = fn;
        var obj = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        obj[0] = (nint)vtable;
        obj[1] = kind;
        _memory.Add((nint)vtable);
        _memory.Add((nint)obj);
        Objects[kind] = (nint)obj;
        return (nint)obj;
    }

    public AdlxLifecycleTests()
    {
        Array.Clear(Live);
        _terminates = _inits = _setResult = _qiResult = _getResult = 0;
        _maxFreq = 2500;
        _qiFillsPointer = false;
        Build(KindSystem, (1, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetGpus), (8, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetTuning), (9, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetPerf));
        Build(KindServices, (8, (nint)(delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)&IsManualSupported), (14, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetManual));
        Build(KindPerf, (18, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetMetrics));
        Build(KindList, (3, (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ListSize), (5, (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ListBegin), (11, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&ListAt));
        Build(KindGpu, (7, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&GpuName));
        Build(KindGeneric, (2, (nint)(delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)&QueryManual));
        Build(KindManual, (6, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&MaxRange), (7, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetMax), (8, (nint)(delegate* unmanaged[Stdcall]<nint, int, int>)&SetMax), (13, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&MaxDefault));
        Build(KindMetrics, (7, (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&Temperature));
    }

    public void Dispose()
    {
        _scope.Dispose();
        foreach (nint p in _memory) NativeMemory.Free((void*)p);
    }

    private static AmdAdlxOverdriveTransport New() => new(
        (nint)(delegate* unmanaged[Cdecl]<ulong*, int>)&QueryVersion,
        (nint)(delegate* unmanaged[Cdecl]<ulong, nint*, int>)&Init,
        (nint)(delegate* unmanaged[Cdecl]<int>)&Terminate);

    private static void AssertNothingLive()
    {
        for (int k = 1; k < Kinds; k++) Assert.True(Live[k] == 0, $"objeto {k} com {Live[k]} referência(s) sobrando");
    }

    [Fact]
    public void Full_cycle_releases_every_interface_once_and_terminates_once()
    {
        var io = New();
        Assert.True(io.TryGetOffsetMhz(out int mhz));
        Assert.Equal(0, mhz);
        Assert.True(io.TrySetOffsetMhz(200));
        Assert.True(io.TryGetOffsetMhz(out mhz));
        Assert.Equal(200, mhz);
        Assert.Equal(55, io.TempC());
        Assert.Equal(0, Live[KindMetrics]);
        Assert.Equal(0, Live[KindList]);
        io.Dispose();
        AssertNothingLive();
        Assert.Equal(1, _inits);
        Assert.Equal(1, _terminates);
        io.Dispose();
        Assert.Equal(1, _terminates);
    }

    [Fact]
    public void Refused_query_interface_leaks_nothing()
    {
        _qiResult = 3;
        var io = New();
        Assert.False(io.TryGetOffsetMhz(out _));
        AssertNothingLive();
        io.Dispose();
    }

    [Fact]
    public void Error_with_a_filled_pointer_is_released_too()
    {
        _qiResult = 3;
        _qiFillsPointer = true;
        var io = New();
        Assert.False(io.TryGetOffsetMhz(out _));
        AssertNothingLive();
        io.Dispose();
    }

    [Fact]
    public void Auto_tuning_is_refused_without_reset_or_retry()
    {
        var io = New();
        _setResult = 18; // ADLX_RESET_NEEDED
        Assert.False(io.TrySetOffsetMhz(200));
        Assert.Contains("Auto Tuning", io.LastError);
        Assert.Equal(1, _inits);
        Assert.Equal(2500, _maxFreq);
        io.Dispose();
    }

    [Fact]
    public void Out_of_range_offset_never_reaches_the_driver()
    {
        var io = New();
        Assert.False(io.TrySetOffsetMhz(int.MaxValue)); // padrão + int.MaxValue estouraria um int
        Assert.False(io.TrySetOffsetMhz(5000));
        Assert.Equal(2500, _maxFreq);
        io.Dispose();
    }

    [Fact]
    public void Read_failure_after_a_tdr_reopens_with_balanced_references()
    {
        var io = New();
        Assert.True(io.TryGetOffsetMhz(out _));
        _getResult = 3;
        Assert.False(io.TryGetOffsetMhz(out _));
        Assert.Equal(2, _inits); // uma reabertura só por chamada
        _getResult = 0;
        Assert.True(io.TryGetOffsetMhz(out _));
        io.Dispose();
        AssertNothingLive();
        Assert.Equal(_inits, _terminates);
    }

    [Fact]
    public void Reconnect_releases_everything_and_the_next_call_reopens()
    {
        var io = New();
        Assert.True(io.TryGetOffsetMhz(out _));
        io.Reconnect();
        AssertNothingLive();
        Assert.Equal(1, _terminates);
        Assert.True(io.TrySetOffsetMhz(100));
        Assert.Equal(2, _inits);
        io.Dispose();
        AssertNothingLive();
    }

    [Fact]
    public void Installed_dll_passes_the_signature_check_when_present()
    {
        string path = Path.Combine(Environment.SystemDirectory, "amdadlx64.dll");
        if (!File.Exists(path)) return; // PC sem driver AMD. Só confere o arquivo: nada é carregado
        Assert.Equal(0, CorsairSdkInstaller.WinVerifyTrustFile(path));
    }
}

public class FanLifecycleTests
{
    private static FanChannelView View(Action<float>? set = null, string preset = "custom") =>
        new(new FanOutput("id", "chip", "fan", set ?? (_ => { }), () => { }, () => "1000 RPM"),
            new FanChannelSettings { Preset = preset, Source = "cpu", Custom = "30:20, 70:80" });

    private static FanControlEngine Engine(FanControlEngine.InputReader? read = null, TimeSpan? interval = null) =>
        new(read ?? ((_, _, _) => new FanInputs(50, 60)), () => new FanSettings(), () => false, interval ?? TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task Repeated_start_and_stop_never_overlap_loops()
    {
        int inside = 0, overlaps = 0;
        var v = View(_ =>
        {
            if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlaps);
            Thread.SpinWait(1000);
            Interlocked.Decrement(ref inside);
        });
        var e = Engine();
        e.SetChannels(new[] { v });
        for (int i = 0; i < 30; i++)
        {
            Assert.True(e.Start());
            Assert.True(e.Start()); // idempotente
            await Task.Delay(1);
            Assert.True(await e.StopAsync(TimeSpan.FromSeconds(5)));
            Assert.False(e.Running);
        }
        Assert.Equal(0, overlaps);
    }

    [Fact]
    public async Task Stop_timeout_keeps_it_marked_running_and_refuses_a_second_loop()
    {
        using var hold = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var e = Engine((_, _, _) => { entered.Set(); hold.Wait(TimeSpan.FromSeconds(10)); return new FanInputs(50, 60); });
        e.SetChannels(new[] { View() });
        Assert.True(e.Start());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(await e.StopAsync(TimeSpan.FromMilliseconds(20)));
        Assert.True(e.Running);
        Assert.False(e.Start()); // o laço antigo ainda está vivo
        hold.Set();
        Assert.True(await e.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.False(e.Running);
        Assert.True(e.Start()); // terminou de fato: pode voltar
        Assert.True(await e.StopAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task No_output_is_commanded_after_a_stop_that_timed_out()
    {
        using var hold = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        int commands = 0;
        var e = Engine((_, _, _) => { entered.Set(); hold.Wait(TimeSpan.FromSeconds(10)); return new FanInputs(50, 60); });
        e.SetChannels(new[] { View(_ => Interlocked.Increment(ref commands)) });
        e.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(await e.StopAsync(TimeSpan.FromMilliseconds(20)));
        hold.Set(); // a leitura termina depois do encerramento
        Assert.True(await e.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, commands);
    }

    [Fact]
    public void Stop_without_start_is_harmless_and_dispose_blocks_new_starts()
    {
        var e = Engine();
        Assert.True(e.Stop(TimeSpan.FromSeconds(1)));
        e.Dispose();
        e.Dispose();
        Assert.False(e.Start());
    }

    [Fact]
    public async Task Sensor_exception_does_not_stop_the_loop()
    {
        int reads = 0;
        var e = Engine((_, _, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1) throw new InvalidOperationException("sensor");
            return new FanInputs(50, 60);
        });
        e.SetChannels(new[] { View() });
        e.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) >= 3, TimeSpan.FromSeconds(5)));
        Assert.True(await e.StopAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Momentary_missing_reading_keeps_the_last_valid_value()
    {
        var answers = new Queue<FanInputs>(new[] { new FanInputs(50, null), new FanInputs(null, null) });
        float? last = null;
        var e = Engine((_, _, _) => answers.Dequeue());
        var v = View(value => last = value);
        e.SetChannels(new[] { v });
        e.Step();
        float first = last!.Value;
        e.Step();
        Assert.True(last < 100); // nada de rotação máxima por causa de uma leitura perdida
        Assert.True(Math.Abs(last!.Value - first) < 30);
    }

    [Fact]
    public void Reading_missing_for_too_long_reaches_the_safety_of_the_curve()
    {
        var answers = new Queue<FanInputs>(new[] { new FanInputs(50, null), new FanInputs(null, null) });
        float? last = null;
        var e = Engine((_, _, _) => answers.Dequeue());
        e.Sensors.HoldLastValid = TimeSpan.Zero;
        e.SetChannels(new[] { View(value => last = value) });
        e.Step();
        Thread.Sleep(2);
        e.Step();
        Assert.Equal(100, last); // proteção de sempre: sem temperatura, 100%
    }

    [Fact]
    public void Closed_sensors_command_nothing()
    {
        float? last = null;
        var e = Engine((_, _, _) => null);
        e.SetChannels(new[] { View(value => last = value) });
        e.Step();
        Assert.Null(last);
    }

    [Fact]
    public void Page_model_only_copies_with_the_page_open()
    {
        var page = new FanPageViewModel();
        var v = View();
        v.EngineLive = "50%";
        page.Channels.Add(v);
        var e = Engine();
        page.Publish(e, visible: false);
        Assert.Equal("", v.Live);
        Assert.False(e.LiveWanted);
        page.Publish(e, visible: true);
        Assert.Equal("50%", v.Live);
        Assert.True(e.LiveWanted);
    }

    [Fact]
    public void Engine_has_no_wpf_dependency_in_its_fields()
    {
        foreach (var field in typeof(FanControlEngine).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            Assert.DoesNotContain("System.Windows", field.FieldType.FullName ?? "");
    }
}
