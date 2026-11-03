namespace ThgOptimization.Tests;

public class DriverVersionTests
{
    [Theory]
    [InlineData("31.0.15.4601", "546.01")]
    [InlineData("32.0.15.6094", "560.94")]
    public void NvidiaFriendly_converte_a_versao_do_wmi(string wmi, string esperado) =>
        Assert.Equal(esperado, DriverManager.NvidiaFriendly(wmi));

    [Theory]
    [InlineData("546.01")]    // não tem 4 partes
    [InlineData("1.2.3.4")]   // poucos dígitos
    [InlineData("")]
    public void NvidiaFriendly_devolve_como_veio_quando_nao_reconhece(string wmi) =>
        Assert.Equal(wmi, DriverManager.NvidiaFriendly(wmi));

    [Theory]
    [InlineData("560.94", "546.01", 1)]
    [InlineData("546.01", "560.94", -1)]
    [InlineData("546.01", "546.01", 0)]
    [InlineData("546.01", "abc", 1)]   // texto inválido vale 0
    public void CompareVersions_compara_pelo_valor(string a, string b, int sinal) =>
        Assert.Equal(sinal, Math.Sign(GpuDriverStatus.CompareVersions(a, b)));
}
