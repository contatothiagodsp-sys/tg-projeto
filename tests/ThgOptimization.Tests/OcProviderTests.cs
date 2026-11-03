using ThgOptimization.Core.Oc;
using Xunit;

namespace ThgOptimization.Tests;

/// <summary>Só simulação: nenhum destes testes toca em SMU, MSR, NVML ou ADLX.</summary>
public class OcProviderTests
{
    private sealed class SimSmu : ISmuTransport
    {
        public readonly Dictionary<uint, uint> Regs = new();
        public uint Response = 0x01;
        public int CommandsSent;
        public uint? LastCommand, LastArg0;
        public uint Margin;
        public bool MarginStuck;
        private readonly SmuFamily _f;
        public SimSmu(SmuFamily f) { _f = f; Regs[f.RspAddress] = 1; }
        public bool Read(uint a, out uint v) { Regs.TryGetValue(a, out v); return true; }
        public bool Write(uint a, uint v)
        {
            Regs[a] = v;
            if (a == _f.MsgAddress)
            {
                CommandsSent++;
                LastCommand = v;
                LastArg0 = Regs[_f.ArgAddress];
                if (v == _f.SetAllCoreMargin) { if (!MarginStuck) Margin = LastArg0.Value; }
                else if (v == _f.GetCoreMargin) Regs[_f.ArgAddress] = Margin;
                Regs[_f.RspAddress] = Response;
            }
            return true;
        }
    }

    private sealed class SimMsr : IMsrTransport
    {
        private readonly Dictionary<int, ulong> _planes = new();
        private ulong _pending;
        public int Writes;
        public bool Locked;
        public bool Read(uint m, out ulong v) { v = _pending; return true; }
        public bool Write(uint m, ulong v)
        {
            int plane = (int)((v >> 40) & 0x7);
            if ((v & 0x100000000UL) != 0)
            {
                Writes++;
                if (!Locked) _planes[plane] = v & 0xFFE00000UL;
            }
            else _pending = _planes.GetValueOrDefault(plane);
            return true;
        }
    }

    private sealed class SimNvml : INvmlOcTransport
    {
        public int Offset, SetCalls, FailWith;
        public double? Temp = 60;
        public int GetGpcOffset(out int mhz) { mhz = Offset; return 0; }
        public int SetGpcOffset(int mhz) { SetCalls++; if (FailWith != 0) return FailWith; Offset = mhz; return 0; }
        public double? TempC() => Temp;
    }

    private sealed class SimAdlx : IAmdOverdriveTransport
    {
        public int Mhz, SetCalls;
        public double? Temp = 60;
        public bool Refuses;
        public bool TryGetOffsetMhz(out int mhz) { mhz = Mhz; return true; }
        public bool TrySetOffsetMhz(int mhz) { SetCalls++; if (Refuses) return false; Mhz = mhz; return true; }
        public double? TempC() => Temp;
    }

    private static OcTelemetry Cool => new(60, 1.1, 1.1);

    // ---------------- trava ----------------

    [Fact]
    public void Gate_lets_a_real_provider_reach_the_transport()
    {
        using var scope = OcGate.AllowInTests(); // 1.6.6: a trava real está fechada; aqui se testa o caminho, com transporte simulado
        var sim = new SimNvml();
        var p = new NvidiaGpuProvider(sim, isSimulated: false);
        Assert.Equal(OcStatus.Applied, p.Apply(new OcRequest(OcVendor.NvidiaGpu, 100)).Status);
        Assert.Equal(100, sim.Offset);
        Assert.Equal(OcStatus.Restored, p.Restore().Status);
        Assert.Equal(0, sim.Offset);
    }

    [Fact]
    public void Gate_constant_is_off_in_this_release()
    {
        Assert.False(OcGate.RealHardwareEnabled);
        Assert.NotNull(OcGate.Block(isSimulated: false));
        Assert.Null(OcGate.Block(isSimulated: true));
    }

    // ---------------- limites ----------------

    [Theory]
    [InlineData(OcVendor.AmdCpu, 1, false)]
    [InlineData(OcVendor.AmdCpu, 0, true)]
    [InlineData(OcVendor.AmdCpu, -30, true)]
    [InlineData(OcVendor.AmdCpu, -31, false)]
    [InlineData(OcVendor.IntelCpu, 1, false)]
    [InlineData(OcVendor.IntelCpu, -150, true)]
    [InlineData(OcVendor.IntelCpu, -151, false)]
    [InlineData(OcVendor.NvidiaGpu, 0, true)]
    [InlineData(OcVendor.NvidiaGpu, 99, false)]
    [InlineData(OcVendor.NvidiaGpu, 100, true)]
    [InlineData(OcVendor.NvidiaGpu, 300, true)]
    [InlineData(OcVendor.NvidiaGpu, 301, false)]
    [InlineData(OcVendor.NvidiaGpu, -100, false)]
    [InlineData(OcVendor.AmdGpu, 0, true)]
    [InlineData(OcVendor.AmdGpu, 99, false)]
    [InlineData(OcVendor.AmdGpu, 100, true)]
    [InlineData(OcVendor.AmdGpu, 250, true)]
    [InlineData(OcVendor.AmdGpu, 251, false)]
    [InlineData(OcVendor.AmdGpu, 300, false)]
    [InlineData(OcVendor.AmdGpu, 301, false)]
    [InlineData(OcVendor.AmdGpu, -100, false)]
    public void Value_limits(OcVendor vendor, int value, bool allowed)
    {
        Assert.Equal(allowed, OcLimits.CheckValue(new OcRequest(vendor, value)) == null);
    }

    [Theory]
    [InlineData(251, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(1000, false)]
    [InlineData(-100, false)]
    [InlineData(50, false)]
    public void Amd_gpu_confirmation_never_passes_the_hard_limit(int mhz, bool allowed)
    {
        Assert.Equal(allowed, OcLimits.CheckValue(new OcRequest(OcVendor.AmdGpu, mhz, AboveSafeConfirmed: true)) == null);
    }

    [Fact]
    public void Confirmation_does_not_change_the_nvidia_limit()
    {
        Assert.NotNull(OcLimits.CheckValue(new OcRequest(OcVendor.NvidiaGpu, 301, AboveSafeConfirmed: true)));
    }

    [Theory]
    [InlineData(OcVendor.AmdCpu, 89.9, 1.35, true)]
    [InlineData(OcVendor.AmdCpu, 90, 1.2, false)]
    [InlineData(OcVendor.AmdCpu, 70, 1.36, false)]
    [InlineData(OcVendor.IntelCpu, 94.9, 1.35, true)]
    [InlineData(OcVendor.IntelCpu, 95, 1.2, false)]
    [InlineData(OcVendor.IntelCpu, 70, 1.36, false)]
    [InlineData(OcVendor.NvidiaGpu, 82.9, null, true)]
    [InlineData(OcVendor.NvidiaGpu, 83, null, false)]
    [InlineData(OcVendor.AmdGpu, 84.9, null, true)]
    [InlineData(OcVendor.AmdGpu, 85, null, false)]
    public void Telemetry_limits(OcVendor vendor, double temp, double? volts, bool allowed)
    {
        Assert.Equal(allowed, OcLimits.CheckTelemetry(vendor, new OcTelemetry(temp, volts, vendor == OcVendor.AmdCpu ? 1.1 : null)) == null);
    }

    // ---------------- fail-closed (T76) ----------------

    [Theory]
    [InlineData(OcVendor.AmdCpu, null, 1.1, 1.1, "Temperatura")]
    [InlineData(OcVendor.AmdCpu, double.NaN, 1.1, 1.1, "Temperatura")]
    [InlineData(OcVendor.AmdCpu, double.PositiveInfinity, 1.1, 1.1, "Temperatura")]
    [InlineData(OcVendor.AmdCpu, 60.0, null, 1.1, "Vcore")]
    [InlineData(OcVendor.AmdCpu, 60.0, double.NaN, 1.1, "Vcore")]
    [InlineData(OcVendor.AmdCpu, 60.0, 1.1, null, "VSOC")]
    [InlineData(OcVendor.AmdCpu, 60.0, 1.1, double.NaN, "VSOC")]
    [InlineData(OcVendor.IntelCpu, 60.0, null, null, "Vcore")]
    [InlineData(OcVendor.IntelCpu, 60.0, double.NegativeInfinity, null, "Vcore")]
    [InlineData(OcVendor.NvidiaGpu, double.NaN, null, null, "Temperatura")]
    [InlineData(OcVendor.AmdGpu, null, null, null, "Temperatura")]
    public void Missing_or_invalid_reading_refuses(OcVendor vendor, double? temp, double? volts, double? vsoc, string what)
    {
        Assert.Contains(what, OcLimits.CheckTelemetry(vendor, new OcTelemetry(temp, volts, vsoc)));
    }

    [Theory]
    [InlineData(5.0, true)]
    [InlineData(5.001, false)]
    [InlineData(60.0, false)]
    [InlineData(-1.0, false)]
    public void Old_sample_refuses(double ageSeconds, bool allowed)
    {
        Assert.Equal(allowed, OcLimits.CheckTelemetry(OcVendor.AmdCpu, new OcTelemetry(60, 1.1, 1.1, TimeSpan.FromSeconds(ageSeconds))) == null);
    }

    [Fact]
    public void Sample_that_was_never_read_refuses()
    {
        Assert.Contains("antiga", OcLimits.CheckTelemetry(OcVendor.NvidiaGpu, new OcTelemetry(60, null, null, TimeSpan.MaxValue)));
    }

    [Fact]
    public void Missing_vsoc_sends_nothing_to_the_smu()
    {
        var sim = new SimSmu(SmuFamily.Zen5);
        var r = new AmdCpuProvider(sim, SmuFamily.Zen5, () => new OcTelemetry(60, 1.1), true).Apply(new OcRequest(OcVendor.AmdCpu, -10));
        Assert.Equal(OcStatus.RejectedByLimit, r.Status);
        Assert.Equal(0, sim.CommandsSent);
    }

    [Fact]
    public void Sensors_are_read_while_holding_the_hardware_lock()
    {
        var sim = new SimSmu(SmuFamily.Zen5);
        bool? held = null;
        AmdCpuProvider? provider = null;
        provider = new AmdCpuProvider(sim, SmuFamily.Zen5, () => { held = provider!.IoLockHeld; return Cool; }, true);
        Assert.True(provider.Apply(new OcRequest(OcVendor.AmdCpu, -10)).Ok);
        Assert.True(held);
    }

    [Fact]
    public void Sensors_are_read_before_anything_reaches_the_smu()
    {
        var sim = new SimSmu(SmuFamily.Zen5);
        int commandsAtRead = -1;
        var provider = new AmdCpuProvider(sim, SmuFamily.Zen5, () => { commandsAtRead = sim.CommandsSent; return Cool; }, true);
        Assert.True(provider.Apply(new OcRequest(OcVendor.AmdCpu, -10)).Ok);
        Assert.Equal(0, commandsAtRead);
    }

    [Theory]
    [InlineData(1.25, true)]
    [InlineData(1.251, false)]
    [InlineData(null, false)]
    public void Vsoc_limit_applies_to_amd_cpu(double? vsoc, bool allowed)
    {
        Assert.Equal(allowed, OcLimits.CheckTelemetry(OcVendor.AmdCpu, new OcTelemetry(60, 1.1, vsoc)) == null);
    }

    [Fact]
    public void Vsoc_limit_does_not_apply_to_other_vendors()
    {
        Assert.Null(OcLimits.CheckTelemetry(OcVendor.IntelCpu, new OcTelemetry(60, 1.1, 1.4)));
    }

    [Fact]
    public void High_vsoc_sends_nothing_to_the_smu()
    {
        var sim = new SimSmu(SmuFamily.Zen5);
        var r = new AmdCpuProvider(sim, SmuFamily.Zen5, () => new OcTelemetry(60, 1.1, 1.3), true).Apply(new OcRequest(OcVendor.AmdCpu, -10));
        Assert.Equal(OcStatus.RejectedByLimit, r.Status);
        Assert.Equal(0, sim.CommandsSent);
    }

    [Fact]
    public void Missing_temperature_refuses_the_change()
    {
        var sim = new SimNvml { Temp = null };
        var r = new NvidiaGpuProvider(sim, isSimulated: true).Apply(new OcRequest(OcVendor.NvidiaGpu, 100));
        Assert.Equal(OcStatus.RejectedByLimit, r.Status);
        Assert.Equal(0, sim.SetCalls);
    }

    [Fact]
    public void Wrong_vendor_is_unsupported()
    {
        Assert.Equal(OcStatus.Unsupported, new NvidiaGpuProvider(new SimNvml(), true).Apply(new OcRequest(OcVendor.AmdCpu, -10)).Status);
    }

    // ---------------- AMD CPU ----------------

    [Theory]
    [InlineData(-30, 0xFFE2u)]
    [InlineData(-1, 0xFFFFu)]
    [InlineData(0, 0u)]
    public void Curve_optimizer_margin_encoding(int counts, uint expected)
    {
        Assert.Equal(expected, SmuMailbox.EncodeMargin(counts));
    }

    [Theory]
    [InlineData(0xFFE2u, -30)]
    [InlineData(0xFFFFFFFBu, -5)]
    [InlineData(0u, 0)]
    public void Curve_optimizer_margin_decoding(uint raw, int counts)
    {
        Assert.Equal(counts, SmuMailbox.DecodeMargin(raw));
    }

    [Fact]
    public void Amd_real_mode_reads_the_margin_back_from_the_smu()
    {
        using var scope = OcGate.AllowInTests();
        using var mutex = new HardwareMutex(@"Local\thg-teste-" + Guid.NewGuid().ToString("N"));
        var sim = new SimSmu(SmuFamily.Zen5) { Margin = SmuMailbox.EncodeMargin(-5) };
        var p = new AmdCpuProvider(sim, SmuFamily.Zen5, () => Cool, isSimulated: false, mutex);
        Assert.Equal(OcStatus.Applied, p.Apply(new OcRequest(OcVendor.AmdCpu, -20)).Status);
        Assert.Equal(SmuFamily.Zen5.GetCoreMargin, sim.LastCommand);
        Assert.Equal(SmuMailbox.EncodeMargin(-20), sim.Margin);
        Assert.True(p.Restore().Ok);
        Assert.Equal(SmuMailbox.EncodeMargin(-5), sim.Margin); // a margem de antes, lida no hardware: não o zero
    }

    [Fact]
    public void Amd_real_mode_undoes_when_the_read_back_does_not_match()
    {
        using var scope = OcGate.AllowInTests();
        using var mutex = new HardwareMutex(@"Local\thg-teste-" + Guid.NewGuid().ToString("N"));
        var sim = new SimSmu(SmuFamily.Zen4) { MarginStuck = true }; // o SMU responde OK, mas a margem não muda
        var r = new AmdCpuProvider(sim, SmuFamily.Zen4, () => Cool, isSimulated: false, mutex).Apply(new OcRequest(OcVendor.AmdCpu, -20));
        Assert.Equal(OcStatus.Failed, r.Status);
    }

    [Fact]
    public void Zen3_real_mode_refuses_before_writing_without_a_simple_read()
    {
        using var scope = OcGate.AllowInTests();
        using var mutex = new HardwareMutex(@"Local\thg-teste-" + Guid.NewGuid().ToString("N"));
        var sim = new SimSmu(SmuFamily.Zen3);
        var r = new AmdCpuProvider(sim, SmuFamily.Zen3, () => Cool, isSimulated: false, mutex).Apply(new OcRequest(OcVendor.AmdCpu, -10));
        Assert.Equal(OcStatus.Failed, r.Status);
        Assert.Equal(0, sim.CommandsSent);
    }

    [Fact]
    public void Hardware_mutex_is_recursive_on_the_same_thread()
    {
        using var mutex = new HardwareMutex(@"Local\thg-teste-" + Guid.NewGuid().ToString("N"));
        Assert.True(mutex.TryEnter(TimeSpan.FromSeconds(1)));
        Assert.True(mutex.TryEnter(TimeSpan.FromSeconds(1)));
        mutex.Exit();
        mutex.Exit();
    }

    [Theory]
    [InlineData("SoC (SVI2 TFN)", true)]
    [InlineData("CPU VSOC", true)]
    [InlineData("VDDCR_SOC", true)]
    [InlineData("Core (SVI2 TFN)", false)]
    [InlineData("CPU VCore", false)]
    public void Vsoc_sensor_names(string name, bool isVsoc)
    {
        Assert.Equal(isVsoc, SensorMonitor.IsVsocSensor(name));
    }

    [Fact]
    public void Amd_sends_all_core_margin_on_the_family_mailbox_and_restores()
    {
        var sim = new SimSmu(SmuFamily.Zen5);
        var p = new AmdCpuProvider(sim, SmuFamily.Zen5, () => Cool, isSimulated: true);
        Assert.True(p.Apply(new OcRequest(OcVendor.AmdCpu, -20)).Ok);
        Assert.Equal(SmuFamily.Zen5.SetAllCoreMargin, sim.LastCommand);
        Assert.Equal(0xFFECu, sim.LastArg0);
        Assert.True(p.Restore().Ok);
        Assert.Equal(0u, sim.LastArg0);
    }

    [Fact]
    public void Amd_smu_rejection_fails_without_claiming_success()
    {
        var sim = new SimSmu(SmuFamily.Zen4) { Response = (uint)SmuStatus.RejectedPrereq };
        var r = new AmdCpuProvider(sim, SmuFamily.Zen4, () => Cool, true).Apply(new OcRequest(OcVendor.AmdCpu, -10));
        Assert.Equal(OcStatus.Failed, r.Status);
    }

    [Fact]
    public void Amd_hot_cpu_sends_nothing()
    {
        var sim = new SimSmu(SmuFamily.Zen3);
        var r = new AmdCpuProvider(sim, SmuFamily.Zen3, () => new OcTelemetry(90, 1.1), true).Apply(new OcRequest(OcVendor.AmdCpu, -10));
        Assert.Equal(OcStatus.RejectedByLimit, r.Status);
        Assert.Equal(0, sim.CommandsSent);
    }

    // ---------------- Intel ----------------

    [Fact]
    public void Intel_encoding_matches_the_known_value()
    {
        Assert.Equal(0x80000011F0000000UL, IntelCpuProvider.EncodeWrite(IntelCpuProvider.PlaneCore, -125));
        Assert.Equal(0x80000211F0000000UL, IntelCpuProvider.EncodeWrite(IntelCpuProvider.PlaneCache, -125));
    }

    [Theory]
    [InlineData(-150)]
    [InlineData(-100)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Intel_encoding_round_trips(int mv)
    {
        ulong raw = IntelCpuProvider.EncodeWrite(0, mv) & 0xFFE00000UL;
        Assert.Equal(mv, IntelCpuProvider.DecodeMillivolts(raw));
    }

    [Fact]
    public void Intel_applies_to_both_planes_and_restores()
    {
        var sim = new SimMsr();
        var p = new IntelCpuProvider(sim, () => Cool, isSimulated: true);
        Assert.True(p.Apply(new OcRequest(OcVendor.IntelCpu, -100)).Ok);
        Assert.Equal(2, sim.Writes);
        Assert.True(p.Restore().Ok);
    }

    [Fact]
    public void Intel_locked_undervolt_is_detected_by_read_back()
    {
        var sim = new SimMsr { Locked = true };
        var r = new IntelCpuProvider(sim, () => Cool, true).Apply(new OcRequest(OcVendor.IntelCpu, -50));
        Assert.Equal(OcStatus.Failed, r.Status);
    }

    // ---------------- GPUs ----------------

    [Fact]
    public void Nvidia_applies_and_restores_the_original_offset()
    {
        var sim = new SimNvml { Offset = 25 };
        var p = new NvidiaGpuProvider(sim, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.NvidiaGpu, 200)).Ok);
        Assert.Equal(200, sim.Offset);
        Assert.True(p.Apply(new OcRequest(OcVendor.NvidiaGpu, 250)).Ok);
        Assert.True(p.Restore().Ok);
        Assert.Equal(25, sim.Offset);
    }

    [Fact]
    public void Nvidia_driver_error_returns_failed()
    {
        var sim = new SimNvml { FailWith = 3 };
        Assert.Equal(OcStatus.Failed, new NvidiaGpuProvider(sim, true).Apply(new OcRequest(OcVendor.NvidiaGpu, 100)).Status);
    }

    [Fact]
    public void Amd_gpu_applies_within_limits_and_refuses_when_hot()
    {
        var sim = new SimAdlx();
        var p = new AmdGpuProvider(sim, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.AmdGpu, 250)).Ok);
        sim.Temp = 85;
        Assert.Equal(OcStatus.RejectedByLimit, p.Apply(new OcRequest(OcVendor.AmdGpu, 100)).Status);
        Assert.Equal(250, sim.Mhz);
    }

    [Fact]
    public void Amd_gpu_out_of_range_never_reaches_the_driver()
    {
        var sim = new SimAdlx();
        var p = new AmdGpuProvider(sim, true);
        Assert.Equal(OcStatus.RejectedByLimit, p.Apply(new OcRequest(OcVendor.AmdGpu, 260)).Status);
        Assert.Equal(OcStatus.RejectedByLimit, p.Apply(new OcRequest(OcVendor.AmdGpu, 301, AboveSafeConfirmed: true)).Status);
        Assert.Equal(0, sim.SetCalls);
    }

    [Fact]
    public void Amd_gpu_restores_the_offset_it_found()
    {
        var sim = new SimAdlx { Mhz = 50 };
        var p = new AmdGpuProvider(sim, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.AmdGpu, 200)).Ok);
        Assert.True(p.Restore().Ok);
        Assert.Equal(50, sim.Mhz);
    }

    [Fact]
    public void Amd_gpu_driver_refusal_returns_failed()
    {
        var sim = new SimAdlx { Refuses = true };
        Assert.Equal(OcStatus.Failed, new AmdGpuProvider(sim, true).Apply(new OcRequest(OcVendor.AmdGpu, 100)).Status);
    }

    // ---------------- vigia ----------------

    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0);

    private static (OcRollbackWatchdog Dog, SimNvml Sim, Func<int> Errors, Action<int> SetErrors) Armed()
    {
        var sim = new SimNvml();
        var p = new NvidiaGpuProvider(sim, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.NvidiaGpu, 100)).Ok);
        int errors = 0;
        var dog = new OcRollbackWatchdog(p, () => errors);
        dog.Arm(T0, TimeSpan.FromMinutes(5));
        return (dog, sim, () => errors, e => errors = e);
    }

    [Fact]
    public void Watchdog_stays_quiet_while_healthy()
    {
        var (dog, sim, _, _) = Armed();
        Assert.Null(dog.Tick(T0.AddSeconds(1)));
        Assert.Equal(100, sim.Offset);
        Assert.True(dog.Armed);
    }

    [Fact]
    public void Watchdog_rolls_back_after_three_hot_readings_only()
    {
        var (dog, sim, _, _) = Armed();
        sim.Temp = 90;
        Assert.Null(dog.Tick(T0.AddSeconds(1)));
        Assert.Null(dog.Tick(T0.AddSeconds(2)));
        Assert.NotNull(dog.Tick(T0.AddSeconds(3)));
        Assert.Equal(0, sim.Offset);
        Assert.False(dog.Armed);
    }

    [Fact]
    public void Watchdog_resets_strikes_when_temperature_recovers()
    {
        var (dog, sim, _, _) = Armed();
        sim.Temp = 90;
        dog.Tick(T0.AddSeconds(1));
        dog.Tick(T0.AddSeconds(2));
        sim.Temp = 60;
        dog.Tick(T0.AddSeconds(3));
        sim.Temp = 90;
        Assert.Null(dog.Tick(T0.AddSeconds(4)));
        Assert.Equal(100, sim.Offset);
    }

    [Fact]
    public void Watchdog_rolls_back_on_new_hardware_error()
    {
        var (dog, sim, _, setErrors) = Armed();
        setErrors(1);
        Assert.NotNull(dog.Tick(T0.AddSeconds(1)));
        Assert.Equal(0, sim.Offset);
    }

    [Fact]
    public void Watchdog_rolls_back_without_confirmation_and_keeps_after_confirm()
    {
        var (dog, sim, _, _) = Armed();
        Assert.NotNull(dog.Tick(T0.AddMinutes(5)));
        Assert.Equal(0, sim.Offset);

        var (dog2, sim2, _, _) = Armed();
        dog2.Confirm();
        Assert.Null(dog2.Tick(T0.AddHours(1)));
        Assert.Equal(100, sim2.Offset);
    }

    private static (OcRollbackWatchdog Dog, SimAdlx Sim) ArmedAmdGpu(HardwareErrorMonitor errors)
    {
        var sim = new SimAdlx();
        var p = new AmdGpuProvider(sim, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.AmdGpu, 200)).Ok);
        var dog = new OcRollbackWatchdog(p, errors) { RestoreRetryDelay = TimeSpan.Zero };
        dog.Arm(T0, TimeSpan.FromHours(1));
        return (dog, sim);
    }

    [Theory]
    [InlineData("Microsoft-Windows-WHEA-Logger 18")]
    [InlineData("Display 4101")]
    public void Hardware_error_rolls_back_at_once_without_waiting_for_a_reading(string detail)
    {
        using var errors = new HardwareErrorMonitor(subscribe: false);
        var (dog, sim) = ArmedAmdGpu(errors);
        string? reason = null;
        dog.RolledBack += why => reason = why;
        errors.Report(detail); // sem nenhum Tick
        Assert.True(dog.WaitForRollback(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, sim.Mhz);
        Assert.False(dog.Armed);
        Assert.Contains(detail, reason);
    }

    [Fact]
    public void Hardware_error_rolls_back_even_after_the_user_confirmed()
    {
        using var errors = new HardwareErrorMonitor(subscribe: false);
        var (dog, sim) = ArmedAmdGpu(errors);
        dog.Confirm();
        errors.Report("Display 4101");
        Assert.True(dog.WaitForRollback(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, sim.Mhz);
    }

    [Fact]
    public void Hardware_error_with_the_watchdog_disarmed_changes_nothing()
    {
        using var errors = new HardwareErrorMonitor(subscribe: false);
        var (dog, sim) = ArmedAmdGpu(errors);
        dog.Disarm();
        errors.Report("Display 4101");
        Assert.Equal(200, sim.Mhz);
    }

    [Fact]
    public void Rollback_retries_while_the_driver_is_coming_back_from_a_tdr()
    {
        using var errors = new HardwareErrorMonitor(subscribe: false);
        var (dog, sim) = ArmedAmdGpu(errors);
        sim.Refuses = true;
        string? reason = null;
        dog.RolledBack += why => reason = why;
        errors.Report("Display 4101");
        Assert.True(dog.WaitForRollback(TimeSpan.FromSeconds(5)));
        Assert.Equal(4, sim.SetCalls); // 1 do ajuste + 3 tentativas de desfazer
        Assert.Contains("reinicie o PC", reason);
    }

    [Fact]
    public void Amd_gpu_rolls_back_after_three_readings_at_85_degrees()
    {
        using var errors = new HardwareErrorMonitor(subscribe: false);
        var (dog, sim) = ArmedAmdGpu(errors);
        sim.Temp = 85;
        Assert.Null(dog.Tick(T0.AddSeconds(1)));
        Assert.Null(dog.Tick(T0.AddSeconds(2)));
        Assert.NotNull(dog.Tick(T0.AddSeconds(3)));
        Assert.Equal(0, sim.Mhz);
    }

    [Fact]
    public void Error_query_covers_whea_and_tdr()
    {
        Assert.Contains("Microsoft-Windows-WHEA-Logger", HardwareErrorMonitor.Query);
        Assert.Contains("Display", HardwareErrorMonitor.Query);
        Assert.Contains("4101", HardwareErrorMonitor.Query);
        Assert.Contains("amdkmdag", HardwareErrorMonitor.Query);
    }

    [Fact]
    public async Task Background_monitor_rolls_back_by_itself_when_not_confirmed()
    {
        var sim = new SimNvml();
        var p = new NvidiaGpuProvider(sim, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.NvidiaGpu, 100)).Ok);
        var dog = new OcRollbackWatchdog(p, () => 0) { MonitorInterval = TimeSpan.FromMilliseconds(10) };
        var rolledBack = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        dog.RolledBack += why => rolledBack.TrySetResult(why);
        dog.StartBackgroundMonitor(TimeSpan.Zero);
        var finished = await Task.WhenAny(rolledBack.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(rolledBack.Task, finished);
        Assert.Equal(0, sim.Offset);
        Assert.False(dog.Armed);
    }

    [Fact]
    public async Task Stopping_the_background_monitor_keeps_the_setting()
    {
        var sim = new SimNvml();
        var p = new NvidiaGpuProvider(sim, true);
        Assert.True(p.Apply(new OcRequest(OcVendor.NvidiaGpu, 100)).Ok);
        var dog = new OcRollbackWatchdog(p, () => 0) { MonitorInterval = TimeSpan.FromMilliseconds(10) };
        dog.StartBackgroundMonitor(TimeSpan.FromHours(1));
        dog.StopBackgroundMonitor();
        await Task.Delay(50);
        Assert.False(dog.Armed);
        Assert.Equal(100, sim.Offset);
    }

    // ---------------- análise do setup ----------------

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", CpuOcFamily.AmdZen5)]
    [InlineData("AMD Ryzen 9 7950X", CpuOcFamily.AmdZen4)]
    [InlineData("AMD Ryzen 5 5600X", CpuOcFamily.AmdZen3)]
    [InlineData("AMD Ryzen 7 5700G", CpuOcFamily.AmdOther)]
    [InlineData("AMD Ryzen 5 3600", CpuOcFamily.AmdOther)]
    [InlineData("Intel(R) Core(TM) i9-14900K", CpuOcFamily.IntelUnlocked)]
    [InlineData("Intel(R) Core(TM) i5-12400F", CpuOcFamily.IntelLocked)]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", CpuOcFamily.IntelCoreUltra)]
    [InlineData("", CpuOcFamily.Unknown)]
    public void Cpu_family(string cpu, CpuOcFamily expected)
    {
        Assert.Equal(expected, OcSetupAdvisor.Family(cpu));
    }

    [Fact]
    public void Advice_mentions_the_limits_and_the_x3d_and_raptor_cases()
    {
        Assert.Contains("1,35", OcSetupAdvisor.CpuAdvice("AMD Ryzen 7 9800X3D").Replace('.', ','));
        Assert.Contains("Dois CCDs", OcSetupAdvisor.CpuAdvice("AMD Ryzen 9 9950X3D"));
        Assert.Contains("microcódigo", OcSetupAdvisor.CpuAdvice("Intel(R) Core(TM) i9-13900K"));
        Assert.Contains("Adrenalin", OcSetupAdvisor.GpuAdvice("AMD Radeon RX 9070 XT"));
        Assert.Contains("300", OcSetupAdvisor.GpuAdvice("NVIDIA GeForce RTX 5080"));
    }

    [Fact]
    public void Backend_summary_flags_winring0()
    {
        Assert.Contains("WinRing0", OcSetupAdvisor.BackendSummary(new OcBackends(true, true, false, false, false)));
        Assert.DoesNotContain("WinRing0", OcSetupAdvisor.BackendSummary(new OcBackends(true, false, false, false, true)));
    }
}
