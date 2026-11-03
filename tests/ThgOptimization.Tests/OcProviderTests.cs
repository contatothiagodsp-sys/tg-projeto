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
        public int Percent;
        public double? Temp = 60;
        public bool TryGetOffsetPercent(out int p) { p = Percent; return true; }
        public bool TrySetOffsetPercent(int p) { Percent = p; return true; }
        public double? TempC() => Temp;
    }

    private static OcTelemetry Cool => new(60, 1.1);

    // ---------------- trava ----------------

    [Fact]
    public void Gate_blocks_real_hardware_and_never_reaches_the_transport()
    {
        var sim = new SimNvml();
        var result = new NvidiaGpuProvider(sim, isSimulated: false).Apply(new OcRequest(OcVendor.NvidiaGpu, 100));
        Assert.Equal(OcStatus.BlockedByGate, result.Status);
        Assert.Equal(0, sim.SetCalls);
    }

    [Fact]
    public void Gate_blocks_restore_on_real_hardware_too()
    {
        var sim = new SimNvml();
        Assert.Equal(OcStatus.BlockedByGate, new NvidiaGpuProvider(sim, isSimulated: false).Restore().Status);
        Assert.Equal(0, sim.SetCalls);
    }

    [Fact]
    public void Gate_constant_is_off_in_this_release()
    {
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
    [InlineData(OcVendor.NvidiaGpu, 300, true)]
    [InlineData(OcVendor.NvidiaGpu, 301, false)]
    [InlineData(OcVendor.AmdGpu, 10, true)]
    [InlineData(OcVendor.AmdGpu, 11, false)]
    public void Value_limits(OcVendor vendor, int value, bool allowed)
    {
        Assert.Equal(allowed, OcLimits.CheckValue(new OcRequest(vendor, value)) == null);
    }

    [Theory]
    [InlineData(OcVendor.AmdCpu, 89.9, 1.35, true)]
    [InlineData(OcVendor.AmdCpu, 90, 1.2, false)]
    [InlineData(OcVendor.AmdCpu, 70, 1.36, false)]
    [InlineData(OcVendor.IntelCpu, 94.9, 1.40, true)]
    [InlineData(OcVendor.IntelCpu, 95, 1.2, false)]
    [InlineData(OcVendor.IntelCpu, 70, 1.41, false)]
    [InlineData(OcVendor.NvidiaGpu, 82.9, null, true)]
    [InlineData(OcVendor.NvidiaGpu, 83, null, false)]
    [InlineData(OcVendor.AmdGpu, 84.9, null, true)]
    [InlineData(OcVendor.AmdGpu, 85, null, false)]
    public void Telemetry_limits(OcVendor vendor, double temp, double? volts, bool allowed)
    {
        Assert.Equal(allowed, OcLimits.CheckTelemetry(vendor, new OcTelemetry(temp, volts)) == null);
    }

    [Fact]
    public void Missing_temperature_refuses_the_change()
    {
        var sim = new SimNvml { Temp = null };
        var r = new NvidiaGpuProvider(sim, isSimulated: true).Apply(new OcRequest(OcVendor.NvidiaGpu, 50));
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
        Assert.True(p.Apply(new OcRequest(OcVendor.AmdGpu, 10)).Ok);
        sim.Temp = 85;
        Assert.Equal(OcStatus.RejectedByLimit, p.Apply(new OcRequest(OcVendor.AmdGpu, 5)).Status);
        Assert.Equal(10, sim.Percent);
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
