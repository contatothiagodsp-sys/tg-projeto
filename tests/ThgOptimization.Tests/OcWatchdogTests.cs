using System.IO;
using Xunit;

namespace ThgOptimization.Tests;

public class OcWatchdogTests : IDisposable
{
    private readonly string _lock = Path.Combine(Path.GetTempPath(), $"thg-oc-{Guid.NewGuid():N}.lock");

    public void Dispose()
    {
        if (File.Exists(_lock)) File.Delete(_lock);
    }

    [Fact]
    public void Watch_lasts_thirty_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), OcWatchdog.ClearAfter);
    }

    [Fact]
    public void Lock_left_behind_blocks_and_is_consumed()
    {
        File.WriteAllText(_lock, "x");
        Assert.Contains("travou", OcWatchdog.BlockReason(_lock, _ => false));
        Assert.False(File.Exists(_lock));
        Assert.Null(OcWatchdog.BlockReason(_lock, _ => false));
    }

    [Fact]
    public void Recent_kernel_power_41_blocks()
    {
        string? why = OcWatchdog.BlockReason(_lock, q => q == OcWatchdog.UnexpectedShutdownQuery);
        Assert.Contains("Kernel-Power 41", why);
    }

    [Fact]
    public void Recent_driver_reset_blocks()
    {
        Assert.Contains("TDR", OcWatchdog.BlockReason(_lock, q => q == OcWatchdog.DriverResetQuery));
    }

    [Fact]
    public void Nothing_recent_does_not_block()
    {
        Assert.Null(OcWatchdog.BlockReason(_lock, _ => false));
    }

    [Fact]
    public void Query_asks_for_the_right_event()
    {
        Assert.Contains("EventID=41", OcWatchdog.UnexpectedShutdownQuery);
        Assert.Contains("Microsoft-Windows-Kernel-Power", OcWatchdog.UnexpectedShutdownQuery);
        Assert.Contains("86400000", OcWatchdog.UnexpectedShutdownQuery);
    }

    [Fact]
    public void Both_queries_are_valid_for_the_event_log()
    {
        foreach (var query in new[] { OcWatchdog.UnexpectedShutdownQuery, OcWatchdog.DriverResetQuery })
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName, query));
            reader.ReadEvent()?.Dispose(); // consulta inválida lança EventLogException
        }
    }
}

public class OcRiskTests
{
    private const string Rtx = "NVIDIA:GeForce RTX 4070";

    [Fact]
    public void Same_board_keeps_the_acceptance()
    {
        Assert.True(Core.Oc.OcRisk.IsAccepted(true, Rtx, Core.Oc.OcRisk.HardwareId("NVIDIA", "GeForce RTX 4070")));
    }

    [Fact]
    public void Another_board_asks_again()
    {
        Assert.False(Core.Oc.OcRisk.IsAccepted(true, Rtx, Core.Oc.OcRisk.HardwareId("AMD", "Radeon RX 7800 XT")));
    }

    [Fact]
    public void Never_accepted_asks()
    {
        Assert.False(Core.Oc.OcRisk.IsAccepted(false, Rtx, Rtx));
    }

    [Fact]
    public void Acceptance_without_a_board_id_is_not_valid()
    {
        Assert.False(Core.Oc.OcRisk.IsAccepted(true, null, Rtx));
    }

    [Fact]
    public void Id_ignores_case_and_outer_spaces()
    {
        Assert.Equal(" NVIDIA:Rtx ".Trim(), Core.Oc.OcRisk.HardwareId(" NVIDIA ", " Rtx "));
        Assert.True(Core.Oc.OcRisk.IsAccepted(true, "nvidia:rtx", "NVIDIA:RTX"));
    }
}
