namespace ThgOptimization.Tests;

public class FanCurvesTests
{
    private static readonly List<(double T, double P)> Padrao = new() { (30, 30), (50, 40), (65, 60), (75, 80), (85, 100) };

    [Fact]
    public void ParseCustom_le_e_ordena_os_pontos()
    {
        var pts = FanCurves.ParseCustom("70:90, 30:20; 50:50%");
        Assert.Equal(new List<(double, double)> { (30, 20), (50, 50), (70, 90) }, pts);
    }

    [Fact]
    public void ParseCustom_limita_temperatura_e_porcentagem()
    {
        var pts = FanCurves.ParseCustom("-5:-10, 500:300");
        Assert.Equal(new List<(double, double)> { (0, 0), (110, 100) }, pts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lixo")]
    [InlineData("40:50")]  // um ponto só não é curva
    public void ParseCustom_cai_na_curva_padrao_quando_invalido(string texto) =>
        Assert.Equal(Padrao, FanCurves.ParseCustom(texto));

    [Theory]
    [InlineData(10, 30)]    // abaixo do primeiro ponto
    [InlineData(30, 30)]
    [InlineData(40, 35)]    // interpolação linear entre (30,30) e (50,40)
    [InlineData(70, 70)]    // entre (65,60) e (75,80)
    [InlineData(85, 100)]
    [InlineData(120, 100)]  // acima do último ponto
    public void Eval_interpola_e_respeita_os_limites(double t, double esperado) =>
        Assert.Equal(esperado, FanCurves.Eval(Padrao, t), 6);
}
