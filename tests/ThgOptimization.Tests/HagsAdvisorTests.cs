using ThgOptimization.Core.Performance;

namespace ThgOptimization.Tests;

public class HagsAdvisorTests
{
    private const string Modern = "AMD Ryzen 7 7700X 8-Core Processor";

    private static GpuInfo Gpu(ulong mb) => new() { Name = "GPU", VramBytes = mb << 20 };

    [Fact]
    public void Vram_16gb_recomenda_hags_ligado()
    {
        var r = HagsAdvisor.Evaluate(false, Gpu(16304), Modern);
        Assert.True(r.Recommended);
        Assert.Equal(16, r.VramGb);
    }

    [Theory]
    [InlineData(6144)]
    [InlineData(8192)]
    public void Vram_6gb_recomenda_desligado(ulong mb)
    {
        Assert.False(HagsAdvisor.Evaluate(true, Gpu(mb), Modern).Recommended);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i3-12100F")]
    [InlineData("AMD Ryzen 3 3100 4-Core Processor")]
    [InlineData("Intel(R) Core(TM) i7-8700K")]
    public void Cpu_fraca_com_vram_8gb_recomenda_ligado(string cpu)
    {
        var r = HagsAdvisor.Evaluate(false, Gpu(8192), cpu);
        Assert.True(r.Recommended);
        Assert.Equal("entrada", r.CpuTier);
    }

    /// <summary>12 GB com CPU comum entram em "VRAM de sobra"; abaixo disso, só a CPU de entrada liga.</summary>
    [Fact]
    public void Vram_intermediaria_liga_so_com_cpu_de_entrada()
    {
        Assert.False(HagsAdvisor.Evaluate(true, Gpu(10240), Modern).Recommended);
        Assert.True(HagsAdvisor.Evaluate(true, Gpu(10240), "Intel(R) Core(TM) i3-12100F").Recommended);
        Assert.True(HagsAdvisor.Evaluate(false, Gpu(12227), Modern).Recommended);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sem_vram_conhecida_mantem_como_esta(bool enabled)
    {
        Assert.Equal(enabled, HagsAdvisor.Evaluate(enabled, null, Modern).Recommended);
    }

    [Fact]
    public void Item_igual_a_recomendacao_nao_oferece_mudanca()
    {
        var check = HagsAdvisor.BuildCheck(true, Gpu(16304), Modern, new Tweak { Id = "hags" });
        Assert.Equal(CheckLevel.Ok, check.Level);
        Assert.Equal(FixKind.None, check.Fix);
    }

    /// <summary>Muda todos os jogos e exige reiniciar: o "corrigir tudo" deixa de fora, e o botão pede confirmação.</summary>
    [Fact]
    public void Item_diferente_da_recomendacao_pede_confirmacao_e_fica_fora_do_turbo()
    {
        bool? written = null;
        var tweak = new Tweak { Id = "hags", Write = (on, _) => written = on };
        var check = HagsAdvisor.BuildCheck(true, Gpu(6144), Modern, tweak);
        Assert.Equal(CheckLevel.Warn, check.Level);
        Assert.Equal(FixKind.Auto, check.Fix);
        Assert.False(check.InTurbo);
        Assert.NotEmpty(check.TurboSkip);
        Assert.True(check.NeedsRestart);
        Assert.Equal("", check.AutoFix!(new AppSettings()));
        Assert.False(written);
    }

    [Fact]
    public void Medicao_pendente_executa_apos_reinicio()
    {
        var pending = new PendingMeasurement { Kind = "hags", BenchmarkKind = "gpu", ScoreBefore = 100, StateAtStart = true };
        Assert.Equal(MeasurementStep.RunBenchmark, MeasurementFlow.Decide(pending, stateNow: false));
        Assert.Equal(MeasurementStep.RemindOnce, MeasurementFlow.Decide(pending, stateNow: true));
    }
}
