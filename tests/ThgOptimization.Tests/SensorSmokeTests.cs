namespace ThgOptimization.Tests;

/// <summary>Teste que fala com o hardware real. Um [Fact(Skip=...)] fixo nunca rodaria: este só pula sem THG_HARDWARE_TESTS=1, como no CI.</summary>
internal sealed class ManualHardwareFactAttribute : FactAttribute
{
    public ManualHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("THG_HARDWARE_TESTS") != "1")
            Skip = "manual: exige hardware real e administrador. Ver docs/auditoria-sensores.md";
    }
}

public class SensorSmokeTests
{
    [ManualHardwareFact]
    public async Task Sensor_open_on_this_machine_returns_rows()
    {
        using var monitor = new SensorMonitor();
        await monitor.OpenAsync();
        Assert.True(monitor.IsOpen);
        await monitor.RefreshAsync(light: false);
        Assert.NotEmpty(monitor.Rows);
    }

    [Fact]
    public void Status_says_detecting_until_the_hardware_opens()
    {
        Assert.Equal(SensorStatus.Detecting, SensorStatus.Describe(isOpen: false, rows: 0, DateTime.Now));
    }

    [Fact]
    public void Status_points_to_the_diagnostic_when_open_and_empty()
    {
        Assert.Equal(SensorStatus.Empty, SensorStatus.Describe(isOpen: true, rows: 0, DateTime.Now));
        Assert.Contains("RODAR DIAGNÓSTICO", SensorStatus.Empty);
    }

    [Fact]
    public void Status_shows_count_and_time_when_there_are_rows()
    {
        Assert.Equal("238 sensores · atualizado 14:05:09", SensorStatus.Describe(isOpen: true, rows: 238, new DateTime(2026, 11, 3, 14, 5, 9)));
    }

    [Fact]
    public void Diagnostic_text_has_the_four_lines()
    {
        var lines = SensorStatus.DiagnosticText("Sim", "Não", "motivo", "0.9.6").Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("PawnIO instalado:", lines[0]);
        Assert.StartsWith("PawnIO ativo:", lines[1]);
        Assert.Contains("motivo", lines[2]);
        Assert.EndsWith("0.9.6", lines[3]);
    }
}
