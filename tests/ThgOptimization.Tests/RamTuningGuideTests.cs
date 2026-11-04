using ThgOptimization.Core.Performance;

namespace ThgOptimization.Tests;

public class RamTuningGuideTests
{
    [Fact]
    public void Guia_am5_tem_fclk_2000()
    {
        var advice = RamTuningGuide.For("AMD Ryzen 7 9700X", "DDR5", 6000);
        Assert.Equal("AM5", advice.Platform);
        Assert.Contains(advice.Items, i => i.Label == "FCLK" && i.Value.Contains("2000"));
        Assert.Contains(advice.Items, i => i.Label.Contains("UCLK"));
        Assert.Contains(advice.Items, i => i.Label.Contains("tCL"));
        Assert.Contains(advice.Items, i => i.Label.Contains("VDDIO"));
        Assert.True(advice.Weight > 0);
    }

    [Fact]
    public void Guia_x3d_tem_weight_zero()
    {
        var advice = RamTuningGuide.For("AMD Ryzen 7 9800X3D", "DDR5", 6000);
        Assert.Equal(0, advice.Weight);
        Assert.Contains(advice.Items, i => i.Value.Contains("1%"));
    }

    [Fact]
    public void Guia_intel_tem_gear_2_para_7200()
    {
        var advice = RamTuningGuide.For("Intel(R) Core(TM) i7-14700K", "DDR5", 7200);
        Assert.Equal("Intel", advice.Platform);
        Assert.Equal("Gear 2", advice.Items.Single(i => i.Label == "Gear").Value);
    }

    [Fact]
    public void Guia_am4_nao_fala_em_2000()
    {
        var advice = RamTuningGuide.For("AMD Ryzen 5 5600X", "DDR4", 3200);
        Assert.Equal("AM4", advice.Platform);
        Assert.DoesNotContain(advice.Items, i => i.Label == "FCLK" && i.Value.Contains("2000"));
    }

    [Fact]
    public void Cpu_desconhecida_ainda_tem_o_passo_seguro()
    {
        Assert.Contains("XMP", RamTuningGuide.For("", "", 0).Items[0].Value);
    }

    [Fact]
    public void Texto_do_guia_termina_com_como_voltar()
    {
        string text = RamTuningGuide.For("AMD Ryzen 7 9700X", "DDR5", 6000).Text;
        Assert.Contains("VALIDAR EXPO/XMP", text);
        Assert.Contains("limpe o CMOS", text);
    }

    [Fact]
    public void Validacao_reporta_erros_de_memoria()
    {
        Assert.Contains("NÃO está estável", RamTuningGuide.ValidationMessage(3, 10, TimeSpan.FromMinutes(15), false));
        Assert.Contains("Nenhum erro", RamTuningGuide.ValidationMessage(0, 10, TimeSpan.FromMinutes(15), false));
    }

    [Fact]
    public void Validacao_sem_passadas_ou_interrompida_nao_afirma_estabilidade()
    {
        Assert.Contains("não dá para concluir", RamTuningGuide.ValidationMessage(0, 0, TimeSpan.FromMinutes(1), true));
        Assert.Contains("interrompido", RamTuningGuide.ValidationMessage(0, 2, TimeSpan.FromMinutes(3), true));
    }
}
