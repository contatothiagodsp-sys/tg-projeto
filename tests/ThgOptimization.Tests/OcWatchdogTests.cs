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
    public void Reset_with_both_parts_back_is_complete()
    {
        var (complete, message) = OcWatchdog.DescribeReset(0, 0, Nvml.Explain);
        Assert.True(complete);
        Assert.Equal("GPU de volta ao padrão de fábrica.", message);
    }

    [Theory]
    [InlineData(3, 0, "Limite de energia: o driver não permite este ajuste nesta placa.", "Clock: de volta ao padrão.")]
    [InlineData(0, 4, "Limite de energia: de volta ao padrão.", "Clock: sem permissão")]
    [InlineData(3, 4, "Limite de energia: o driver não permite este ajuste nesta placa.", "Clock: sem permissão")]
    public void Partial_reset_names_each_part_and_is_not_complete(int power, int clock, string powerText, string clockText)
    {
        var (complete, message) = OcWatchdog.DescribeReset(power, clock, Nvml.Explain);
        Assert.False(complete); // o aviso de travamento continua armado: parte do ajuste segue valendo
        Assert.StartsWith("Restauração parcial.", message);
        Assert.Contains(powerText, message);
        Assert.Contains(clockText, message);
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
    public void Acceptance_from_before_the_id_existed_adopts_the_current_board()
    {
        string? id = Core.Oc.OcRisk.MigratedId(true, null, Rtx);
        Assert.Equal(Rtx, id);
        Assert.True(Core.Oc.OcRisk.IsAccepted(true, id, Rtx));
    }

    [Fact]
    public void Migration_never_accepts_for_someone_who_did_not()
    {
        Assert.Null(Core.Oc.OcRisk.MigratedId(false, null, Rtx));
    }

    [Fact]
    public void Migration_keeps_an_id_already_saved()
    {
        Assert.Equal("AMD:outra", Core.Oc.OcRisk.MigratedId(true, "AMD:outra", Rtx));
    }

    [Fact]
    public void Migration_without_a_board_waits()
    {
        Assert.Null(Core.Oc.OcRisk.MigratedId(true, null, ""));
    }

    [Fact]
    public void Id_ignores_case_and_outer_spaces()
    {
        Assert.Equal(" NVIDIA:Rtx ".Trim(), Core.Oc.OcRisk.HardwareId(" NVIDIA ", " Rtx "));
        Assert.True(Core.Oc.OcRisk.IsAccepted(true, "nvidia:rtx", "NVIDIA:RTX"));
    }
}
