using ThgOptimization.Core.Performance;

namespace ThgOptimization.Tests;

public class MsiSecurityItemTests
{
    [Theory]
    [InlineData("Micro-Star International Co., Ltd.", "MPG X870E CARBON WIFI (MS-7E49)")]
    [InlineData("Micro-Star International Co., Ltd.", "MAG B650 TOMAHAWK WIFI (MS-7D75)")]
    [InlineData("MSI", "PRO B850-P WIFI")]
    public void Msi_am5_gera_item_info(string maker, string model)
    {
        var item = MsiSecurityAdvisor.Build(maker, model);
        Assert.NotNull(item);
        Assert.Equal("bios.msi.security", item!.Id);
        Assert.Equal("BIOS", item.Area);
        Assert.Equal(CheckLevel.Info, item.Level);
        Assert.Contains(model, item.Found);
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG STRIX X870E-E GAMING WIFI")]
    [InlineData("Gigabyte Technology Co., Ltd.", "B650 AORUS ELITE AX")]
    [InlineData("Micro-Star International Co., Ltd.", "MAG B550 TOMAHAWK (MS-7C91)")]
    [InlineData("Micro-Star International Co., Ltd.", "PRO Z790-P WIFI")]
    public void Outra_placa_nao_gera(string maker, string model)
    {
        Assert.Null(MsiSecurityAdvisor.Build(maker, model));
    }

    [Fact]
    public void Item_nao_tem_autofix()
    {
        var item = MsiSecurityAdvisor.Build("MSI", "X870E")!;
        Assert.Equal(FixKind.Bios, item.Fix);
        Assert.Null(item.AutoFix);
        Assert.False(item.InTurbo);
    }

    /// <summary>A ligação com o FPS não está comprovada, e o Windows não diz o valor da opção: o texto não pode afirmar nenhum dos dois.</summary>
    [Fact]
    public void Texto_nao_afirma_o_que_nao_foi_comprovado()
    {
        var item = MsiSecurityAdvisor.Build("MSI", "X870E")!;
        Assert.Contains("pode ter", item.Found);
        Assert.Contains("não foi comprovada", item.Advice);
        Assert.Contains("Meça", item.Advice);
        Assert.Contains("Anote o valor atual", item.FixTarget);
    }
}
