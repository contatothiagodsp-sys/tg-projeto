using ThgOptimization.Core.Performance;

namespace ThgOptimization.Tests;

public class SecurityAdvisorTests
{
    private static GpuInfo Gpu(int gb) => new() { Name = "GPU", VramBytes = (ulong)gb << 30 };

    private static PendingMeasurement Pending(bool stateAtStart = true, bool reminded = false) =>
        new() { Kind = "hvci", BenchmarkKind = "rt:alta:1440", ScoreBefore = 100, Timestamp = DateTime.Now, StateAtStart = stateAtStart, Reminded = reminded };

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz")]
    [InlineData("Intel(R) Core(TM) i9-10900K CPU @ 3.70GHz")]
    [InlineData("AMD Ryzen 7 1700X Eight-Core Processor")]
    public void Cpu_antiga_com_hvci_estima_perda_acima_de_10(string cpu)
    {
        var impact = SystemSecurityAdvisor.Estimate(Gpu(16), cpu, hvciOn: true);
        Assert.True(impact.EstimatedFpsLossPct >= 10);
        Assert.True(impact.IsHighRisk);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor")]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K")]
    [InlineData("Intel(R) Core(TM) i5-11400F")]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K")]
    public void Cpu_moderna_com_gpu_forte_estima_abaixo_de_3(string cpu)
    {
        var impact = SystemSecurityAdvisor.Estimate(Gpu(16), cpu, hvciOn: true);
        Assert.True(impact.EstimatedFpsLossPct < 3);
        Assert.False(impact.IsHighRisk);
    }

    [Fact]
    public void Cpu_moderna_com_gpu_de_entrada_fica_na_faixa_moderada()
    {
        var impact = SystemSecurityAdvisor.Estimate(Gpu(6), "AMD Ryzen 5 7600", hvciOn: true);
        Assert.InRange(impact.EstimatedFpsLossPct!.Value, 2, 5);
    }

    [Fact]
    public void Hvci_desligado_nao_estima_perda()
    {
        Assert.Null(SystemSecurityAdvisor.Estimate(Gpu(4), "Intel(R) Core(TM) i7-8700K", hvciOn: false).EstimatedFpsLossPct);
    }

    [Fact]
    public void Texto_diz_que_e_estimativa_e_nao_promete()
    {
        string text = SystemSecurityAdvisor.Estimate(Gpu(16), "AMD Ryzen 7 9800X3D", hvciOn: true).Explanation;
        Assert.Contains("Estimativa", text);
        Assert.Contains("varia por jogo", text);
    }

    [Fact]
    public void Item_nunca_desliga_a_protecao_sozinho()
    {
        var check = SystemSecurityAdvisor.BuildCheck(Gpu(16), "Intel(R) Core(TM) i7-8700K", vbsOn: true, hvciOn: true);
        Assert.Equal(FixKind.Settings, check.Fix);
        Assert.Null(check.AutoFix);
        Assert.False(check.InTurbo);
        Assert.Equal("windowsdefender://coreisolation", check.FixTarget);
        Assert.Equal("MEDIR IMPACTO", check.SecondaryLabel);
        Assert.Contains("perda estimada de 12", check.Found);
        Assert.False(SystemSecurityAdvisor.BuildCheck(Gpu(16), "x", vbsOn: false, hvciOn: false).ShowSecondary);
    }

    [Fact]
    public void Medicao_pendente_com_hvci_ainda_ligado_nao_executa()
    {
        Assert.Equal(MeasurementStep.RemindOnce, MeasurementFlow.Decide(Pending(), stateNow: true));
        Assert.Equal(MeasurementStep.None, MeasurementFlow.Decide(Pending(reminded: true), stateNow: true));
    }

    [Fact]
    public void Medicao_pendente_com_hvci_desligado_executa()
    {
        Assert.Equal(MeasurementStep.RunBenchmark, MeasurementFlow.Decide(Pending(), stateNow: false));
        Assert.Equal(MeasurementStep.None, MeasurementFlow.Decide(null, stateNow: false));
    }

    [Fact]
    public void Medicao_usa_o_benchmark_recente_de_gpu_como_antes()
    {
        var now = new DateTime(2026, 11, 3);
        var records = new[]
        {
            new BenchRecord(now.AddDays(-40), "gpu", 900, "velho"),
            new BenchRecord(now.AddDays(-1), "sim", 500, "outro teste"),
            new BenchRecord(now.AddDays(-2), "rt:alta:1440", 800, "serve"),
        };
        Assert.Equal("rt:alta:1440", MeasurementFlow.Baseline(records, now)!.Test);
        Assert.Null(MeasurementFlow.Baseline(records.Take(2), now));
    }

    [Fact]
    public void Resultado_e_medido_neste_pc()
    {
        Assert.Equal(10, MeasurementFlow.DeltaPct(100, 110), precision: 6);
        Assert.Contains("custava", MeasurementFlow.Message("hvci", 10, false));
        Assert.Contains("sem custo mensurável", MeasurementFlow.Message("hvci", 0.5, false));
        Assert.StartsWith("Medido neste PC", MeasurementFlow.Message("hags", -2, true));
    }

    [Fact]
    public void Medicao_pendente_de_arquivo_antigo_e_descartada()
    {
        var s = new AppSettings { PendingMeasurement = Pending(), PendingTimerMeasurement = TimerMeasurement.Arm("x", DateTime.Now) };
        LegacySettingsMigration.Sanitize(s);
        Assert.Null(s.PendingMeasurement);
        Assert.Null(s.PendingTimerMeasurement);
    }
}
