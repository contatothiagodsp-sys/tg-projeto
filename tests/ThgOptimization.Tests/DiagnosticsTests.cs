using System.Diagnostics;
using System.IO;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class MemoryDiagnosticsTests
{
    [Theory]
    [InlineData(50, 40, MemoryPressureLevel.Low)]
    [InlineData(15, 40, MemoryPressureLevel.Moderate)]
    [InlineData(5, 40, MemoryPressureLevel.High)]
    [InlineData(50, 95, MemoryPressureLevel.High)]
    public void Pressure_levels(double available, double commit, MemoryPressureLevel expected)
    {
        Assert.Equal(expected, MemoryDiagnostics.Classify(available, commit));
    }

    [Fact]
    public void Reads_this_machine()
    {
        var m = MemoryDiagnostics.Read();
        Assert.NotNull(m);
        Assert.True(m!.TotalMb > 0 && m.AvailableMb <= m.TotalMb);
    }

    [Theory]
    [InlineData("""{"MemoryCompression":true,"PageCombining":false}""", true, false)]
    [InlineData("""{"MemoryCompression":false}""", false, null)]
    public void Reads_the_state_of_mmagent(string json, bool? compression, bool? combining)
    {
        var s = MemoryDiagnostics.ParseMmAgent(json)!;
        Assert.Equal(compression, s.MemoryCompression);
        Assert.Equal(combining, s.PageCombining);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Get-MMAgent : acesso negado")]
    [InlineData("[1,2]")]
    public void Unreadable_state_is_null(string? json) => Assert.Null(MemoryDiagnostics.ParseMmAgent(json));

    [Fact]
    public void Only_the_read_command_exists()
    {
        Assert.StartsWith("Get-MMAgent", MemoryDiagnostics.ReadMmAgentCommand);
        foreach (string file in Directory.GetFiles(RepoRoot(), "*.cs", SearchOption.TopDirectoryOnly).Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "Infrastructure"), "*.cs")))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("Disable-MMAgent", text);
            Assert.DoesNotContain("Enable-MMAgent", text);
        }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tHgOptimization.csproj"))) return d.FullName;
        throw new FileNotFoundException("tHgOptimization.csproj");
    }
}

public class InterruptDiagnosticsTests
{
    [Fact]
    public void Entries_are_read_at_the_right_offsets()
    {
        var b = new byte[96];
        BitConverter.TryWriteBytes(b.AsSpan(24), 111L);
        BitConverter.TryWriteBytes(b.AsSpan(32), 222L);
        BitConverter.TryWriteBytes(b.AsSpan(40), 333u);
        BitConverter.TryWriteBytes(b.AsSpan(48 + 24), 444L);
        var list = InterruptDiagnostics.Parse(b);
        Assert.Equal(2, list.Length);
        Assert.Equal(new ProcessorTimes(111, 222, 333), list[0]);
        Assert.Equal(444, list[1].Dpc);
    }

    [Fact]
    public void Percent_and_rate_are_computed_over_the_interval()
    {
        var before = new[] { new ProcessorTimes(0, 0, 100) };
        var after = new[] { new ProcessorTimes(100_000, 50_000, 1100) }; // 10 ms de DPC e 5 ms de ISR em 1 s
        var load = Assert.Single(InterruptDiagnostics.Compute(before, after, 1));
        Assert.Equal(1, load.DpcPercent, 6);
        Assert.Equal(0.5, load.IsrPercent, 6);
        Assert.Equal(1000, load.InterruptsPerSecond, 6);
    }

    [Fact]
    public void Counter_that_wrapped_does_not_go_negative()
    {
        var load = InterruptDiagnostics.Compute(new[] { new ProcessorTimes(0, 0, uint.MaxValue) }, new[] { new ProcessorTimes(0, 0, 9) }, 1)[0];
        Assert.Equal(10, load.InterruptsPerSecond);
    }

    [Fact]
    public void Summary_points_to_the_busiest_cpu()
    {
        var text = InterruptDiagnostics.Summary(new[] { new CpuInterruptLoad(0, 1, 1, 10), new CpuInterruptLoad(5, 9, 1, 20) });
        Assert.Contains("CPU 5", text);
    }

    [Fact]
    public void Reads_one_entry_per_cpu_of_the_current_group()
    {
        var list = InterruptDiagnostics.Read();
        Assert.NotNull(list);
        Assert.Equal(Math.Min(Environment.ProcessorCount, 64), list!.Length);
    }
}

/// <summary>A contagem de handles é do processo inteiro: este teste roda sem os outros em paralelo.</summary>
[CollectionDefinition("Contagem de handles", DisableParallelization = true)]
public class HandleCountCollection { }

[Collection("Contagem de handles")]
public class WmiLifecycleTests
{
    /// <summary>Consulta real, só de leitura. A coleção e cada objeto WMI são liberados dentro do método.</summary>
    [Fact]
    public void Wmi_read_can_be_repeated_without_growing_handles()
    {
        try { NetworkDiagnostics.ReadAdapters(); }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { return; } // ambiente sem o provedor
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        int before = self.HandleCount;
        for (int i = 0; i < 5; i++) NetworkDiagnostics.ReadAdapters();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        self.Refresh();
        Assert.True(self.HandleCount - before < 200, $"handles: {before} → {self.HandleCount}");
    }
}

public class NetworkDiagnosticsTests
{
    [Fact]
    public void Link_quality_counts_loss_and_jitter()
    {
        var q = NetworkDiagnostics.Summarize(new long?[] { 10, 20, null, 10 });
        Assert.Equal(4, q.Sent);
        Assert.Equal(3, q.Received);
        Assert.Equal(25, q.LossPercent, 3);
        Assert.Equal(13.333, q.AvgMs, 2);
        Assert.Equal(10, q.JitterMs, 3);
    }

    [Fact]
    public void No_reply_at_all_gives_full_loss_without_dividing_by_zero()
    {
        var q = NetworkDiagnostics.Summarize(new long?[] { null, null });
        Assert.Equal(100, q.LossPercent);
        Assert.Equal(0, q.AvgMs);
    }

    [Fact]
    public void Property_the_adapter_does_not_expose_is_stated()
    {
        var a = new NetAdapterSettings("Ethernet", "x", true, false, new Dictionary<string, string> { ["*RSS"] = "Habilitado" });
        Assert.Equal("Habilitado", a.Value("*RSS"));
        Assert.Equal("não informado", a.Value("*InterruptModeration"));
    }
}

public class VisualActivityTests
{
    [Fact]
    public void Tray_without_use_slows_the_window_timer_and_coming_back_restores_it()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), UiThrottle.MainTimerInterval(idle: true));
        Assert.Equal(TimeSpan.FromSeconds(1), UiThrottle.MainTimerInterval(idle: false));
    }

    [Fact]
    public void Sensors_on_the_tray_stay_at_one_reading_every_10_seconds()
    {
        var ticks = UiThrottle.MainTimerInterval(idle: true) * UiThrottle.SensorReadEvery(idle: true);
        Assert.Equal(TimeSpan.FromSeconds(10), ticks);
    }

    [Theory]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, true)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(false, false, false, true, false, true)]
    [InlineData(false, false, false, false, true, true)]
    public void Etw_session_only_with_someone_using_it(bool overlay, bool game, bool page, bool session, bool capture, bool expected)
    {
        Assert.Equal(expected, UiThrottle.FrameMonitorNeeded(overlay, game, page, session, capture));
    }
}

public class OverlayProfileTests
{
    [Fact]
    public void Picking_only_frames_does_not_query_sensors_nor_driver()
    {
        var show = new HashSet<string> { "fps", "frametime", "low1", "graph", "p99" };
        Assert.False(OverlayMetrics.NeedsLoadSample(show));
        Assert.False(OverlayMetrics.NeedsVram(show));
        Assert.False(OverlayMetrics.NeedsMemoryStatus(show));
    }

    [Theory]
    [InlineData("gpu_load")]
    [InlineData("cpu_temp")]
    [InlineData("vram_pct")]
    public void Items_that_depend_on_the_sample_request_it(string key) => Assert.True(OverlayMetrics.NeedsLoadSample(new HashSet<string> { key }));

    [Fact]
    public void New_metrics_are_off_by_default()
    {
        var defaults = OverlayMetrics.Defaults();
        foreach (string key in new[] { "low01", "p99", "p999", "stutters_min", "mem_pressure", "present_mode", "etw_state" })
        {
            Assert.Contains(OverlayMetrics.All, m => m.Key == key);
            Assert.DoesNotContain(key, defaults);
        }
    }

    [Fact]
    public void Economy_drops_sensors_and_graph()
    {
        var shown = MonitorProfiles.Effective(new[] { "fps", "graph", "gpu_load", "low01" }, MonitorProfile.Economy);
        Assert.Equal(new HashSet<string> { "fps", "low01" }, shown);
    }

    [Fact]
    public void Balanced_hides_only_what_is_marked_as_detailed()
    {
        var shown = MonitorProfiles.Effective(new[] { "fps", "gpu_load", "p99", "p999", "etw_state" }, MonitorProfile.Balanced);
        Assert.Equal(new HashSet<string> { "fps", "gpu_load", "p99" }, shown);
    }

    [Fact]
    public void A_profile_never_adds_what_the_user_did_not_choose()
    {
        Assert.Equal(new HashSet<string> { "fps" }, MonitorProfiles.Effective(new[] { "fps" }, MonitorProfile.Detailed));
    }

    [Fact]
    public void Screen_rhythm_follows_the_profile_and_the_old_rhythm_is_the_default()
    {
        Assert.Equal(MonitorProfile.Balanced, MonitorProfiles.Parse(new AppSettings().MonitorProfile));
        Assert.Equal(MonitorProfile.Balanced, MonitorProfiles.Parse(null));
        Assert.Equal(500, MonitorProfiles.Interval(MonitorProfile.Balanced).TotalMilliseconds);
        Assert.Equal(1000, MonitorProfiles.Interval(MonitorProfile.Economy).TotalMilliseconds);
        Assert.Equal(250, MonitorProfiles.Interval(MonitorProfile.Detailed).TotalMilliseconds);
    }

    [Theory]
    [InlineData(MonitorProfile.Economy)]
    [InlineData(MonitorProfile.Balanced)]
    [InlineData(MonitorProfile.Detailed)]
    public void Key_round_trips(MonitorProfile p) => Assert.Equal(p, MonitorProfiles.Parse(MonitorProfiles.Key(p)));
}

public class LightPumpAllocationTests
{
    private sealed class Quiet : ILightBackend
    {
        public string Name => "q";
        public int MinIntervalMs => 0;
        public LightBackendContract Contract => LightBackendContract.NormalizedRgb;
        public int Acquire(LightDevice d, bool exclusive) => 0;
        public int Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors) => 0;
        public void Release(LightDevice d) { }
        public void EndSession() { }
        public string ErrorText(int e) => "";
        public void Shutdown() { }
    }

    [Fact]
    public void Posting_a_frame_after_the_first_one_does_not_allocate()
    {
        using var pump = new LightPump();
        var d = new LightDevice { Backend = new Quiet(), Id = "a", Name = "a", LedX = new double[60] };
        var colors = new (byte, byte, byte)[60];
        Action<int> done = _ => { };
        // Os dois buffers do aparelho nascem nos primeiros quadros, e a primeira disputa pela trava também tem custo único.
        for (int i = 0; i < 2000; i++) pump.Post(d, colors, done);
        Thread.Sleep(20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) pump.Post(d, colors, done);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1000, $"{allocated} bytes em 1000 quadros"); // a 1.6.4 alocava o vetor de cores e o callback em cada um
    }

    [Fact]
    public void Key_of_a_device_is_calculated_once()
    {
        var d = new LightDevice { Backend = new Quiet(), Id = "a" };
        Assert.Same(d.Key, d.Key);
    }
}
