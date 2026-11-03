namespace ThgOptimization.Tests;

public class OptimizerTrimTests
{
    [Theory]
    [InlineData("VALORANT-Win64-Shipping")]
    [InlineData("VALORANT")]
    [InlineData("vgc")]
    [InlineData("vgtray")]
    [InlineData("vgm")]
    [InlineData("valorant-win64-shipping")]
    public void Valorant_e_Vanguard_nunca_sao_abertos(string name) => Assert.False(Optimizer.CanTrim(name));

    [Theory]
    [InlineData("dwm")]
    [InlineData("csrss")]
    [InlineData("EasyAntiCheat")]
    public void Lista_de_sempre_continua_valendo(string name) => Assert.False(Optimizer.CanTrim(name));

    [Theory]
    [InlineData("chrome")]
    [InlineData("Spotify")]
    public void Programas_comuns_continuam_sendo_enxugados(string name) => Assert.True(Optimizer.CanTrim(name));
}
