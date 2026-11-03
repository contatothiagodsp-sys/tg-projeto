using ThgOptimization.Core.Oc;
using Xunit;

namespace ThgOptimization.Tests;

/// <summary>Driver simulado: nada aqui chega à NVML.</summary>
public class GpuOcTransactionTests
{
    private sealed class SimGpu : IGpuTuningDevice
    {
        public uint Power = 200_000;
        public uint Clock = 1500;
        public uint? LockedCap;
        public int LockCode, ResetCode;
        /// <summary>Responde 0 e fica com este valor: é o caso de BIOS travada.</summary>
        public uint? SilentlyAppliesPower;
        public uint? ClockAfterLock;
        public bool ClockUnreadable, PowerUnreadable;
        public int PowerSets, Locks, Resets;
        public readonly List<uint> PowerWrites = new();

        public uint? PowerLimitMw() => PowerUnreadable ? null : Power;
        public int SetPowerLimitMw(uint mw)
        {
            PowerSets++;
            PowerWrites.Add(mw);
            Power = SilentlyAppliesPower is uint forced && PowerSets == 1 ? forced : mw;
            return 0;
        }
        public uint? ClockMhz() => ClockUnreadable ? null : ClockAfterLock ?? Clock;
        public int LockClocks(uint minMhz, uint maxMhz)
        {
            Locks++;
            if (LockCode != 0) return LockCode;
            LockedCap = maxMhz;
            return 0;
        }
        public int ResetClocks()
        {
            Resets++;
            if (ResetCode != 0) return ResetCode;
            LockedCap = null;
            return 0;
        }
    }

    private static readonly Action<TimeSpan> NoWait = _ => { };

    [Fact]
    public void Confirmed_change_reports_applied()
    {
        var gpu = new SimGpu();
        var r = GpuOcTransaction.Apply(gpu, 180_000, 1800, null, NoWait);
        Assert.True(r.Applied);
        Assert.Equal(180_000u, gpu.Power);
        Assert.Equal(1800u, gpu.LockedCap);
    }

    [Fact]
    public void Driver_that_accepts_but_ignores_the_power_limit_forces_rollback()
    {
        var gpu = new SimGpu { SilentlyAppliesPower = 150_000 };
        var r = GpuOcTransaction.Apply(gpu, 250_000, 1800, null, NoWait);
        Assert.False(r.Applied);
        Assert.True(r.Restored);
        Assert.Contains("aplicou 150 W", r.Message);
        Assert.Equal(200_000u, gpu.Power);
        Assert.Equal(0, gpu.Locks); // não chega a travar o clock
    }

    [Fact]
    public void Clock_above_the_cap_undoes_power_and_clock()
    {
        var gpu = new SimGpu { ClockAfterLock = 2100 };
        var r = GpuOcTransaction.Apply(gpu, 180_000, 1800, 1900, NoWait);
        Assert.False(r.Applied);
        Assert.True(r.Restored);
        Assert.Contains("2100 MHz", r.Message);
        Assert.Equal(200_000u, gpu.Power);
        Assert.Equal(1900u, gpu.LockedCap); // o teto anterior volta
    }

    [Fact]
    public void Clock_gets_time_to_fall_before_failing()
    {
        var gpu = new SimGpu { ClockAfterLock = 2100 };
        int waits = 0;
        var r = GpuOcTransaction.Apply(gpu, 180_000, 1800, null, _ => { if (++waits == 2) gpu.ClockAfterLock = 1750; });
        Assert.True(r.Applied);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void Clock_inside_the_tolerance_passes()
    {
        var gpu = new SimGpu { ClockAfterLock = 1850 };
        Assert.True(GpuOcTransaction.Apply(gpu, 180_000, 1800, null, NoWait).Applied);
    }

    [Fact]
    public void Unreadable_clock_is_not_confirmation()
    {
        var gpu = new SimGpu { ClockUnreadable = true };
        var r = GpuOcTransaction.Apply(gpu, 180_000, 1800, null, NoWait);
        Assert.False(r.Applied);
        Assert.Equal(200_000u, gpu.Power);
    }

    [Fact]
    public void Unreadable_state_changes_nothing()
    {
        var gpu = new SimGpu { PowerUnreadable = true };
        var r = GpuOcTransaction.Apply(gpu, 180_000, 1800, null, NoWait);
        Assert.False(r.Applied);
        Assert.Equal(0, gpu.PowerSets);
        Assert.Equal(0, gpu.Locks);
    }

    [Fact]
    public void Refused_lock_restores_the_power_limit()
    {
        var gpu = new SimGpu { LockCode = 4 };
        var r = GpuOcTransaction.Apply(gpu, 180_000, 1800, null, NoWait);
        Assert.False(r.Applied);
        Assert.True(r.Restored);
        Assert.Equal(200_000u, gpu.Power);
    }

    [Fact]
    public void Failed_rollback_is_reported_honestly()
    {
        var gpu = new SimGpu { ClockAfterLock = 2100 };
        var failing = new FailsSecondPowerWrite(gpu);
        var r = GpuOcTransaction.Apply(failing, 180_000, 1800, null, NoWait);
        Assert.False(r.Applied);
        Assert.False(r.Restored);
        Assert.Contains("reinicie o PC", r.Message);
    }

    private sealed class FailsSecondPowerWrite : IGpuTuningDevice
    {
        private readonly SimGpu _gpu;
        private int _writes;
        public FailsSecondPowerWrite(SimGpu gpu) => _gpu = gpu;
        public uint? PowerLimitMw() => _gpu.PowerLimitMw();
        public int SetPowerLimitMw(uint mw) => ++_writes > 1 ? 999 : _gpu.SetPowerLimitMw(mw);
        public uint? ClockMhz() => _gpu.ClockMhz();
        public int LockClocks(uint minMhz, uint maxMhz) => _gpu.LockClocks(minMhz, maxMhz);
        public int ResetClocks() => _gpu.ResetClocks();
    }

    [Fact]
    public void Board_without_clock_lock_support_can_still_change_power()
    {
        var gpu = new SimGpu { ResetCode = 3 };
        var r = GpuOcTransaction.Apply(gpu, 180_000, null, null, NoWait);
        Assert.True(r.Applied);
        Assert.Equal(180_000u, gpu.Power);
    }

    [Fact]
    public void Removing_a_cap_of_ours_that_the_driver_refuses_is_a_failure()
    {
        var gpu = new SimGpu { ResetCode = 4, LockedCap = 1800 };
        var r = GpuOcTransaction.Apply(gpu, 180_000, null, 1800, NoWait);
        Assert.False(r.Applied);
        Assert.Equal(200_000u, gpu.Power);
    }
}
