namespace ThgOptimization.Tests;

public class GameLauncherArgsTests
{
    private static string Args(string style, string mode = "default", string res = "default", int hz = 0, string extra = "") =>
        GameLauncher.BuildArgs(new GameProfile { Style = style, WinMode = mode, Res = res, Hz = hz, Extra = extra });

    [Theory]
    [InlineData("full", "-fullscreen")]
    [InlineData("window", "-windowed")]
    [InlineData("borderless", "-windowed -noborder")]
    [InlineData("default", "")]
    public void Source_modos_de_janela(string mode, string expected) => Assert.Equal(expected, Args("source", mode));

    [Theory]
    [InlineData("full", "-screen-fullscreen 1")]
    [InlineData("window", "-screen-fullscreen 0")]
    [InlineData("borderless", "-popupwindow")]
    [InlineData("default", "")]
    public void Unity_modos_de_janela(string mode, string expected) => Assert.Equal(expected, Args("unity", mode));

    [Theory]
    [InlineData("full", "-fullscreen")]
    [InlineData("window", "-windowed")]
    [InlineData("borderless", "-windowed -noborder")]
    [InlineData("default", "")]
    public void Unreal_modos_de_janela(string mode, string expected) => Assert.Equal(expected, Args("unreal", mode));

    [Theory]
    [InlineData("source", "-w 1920 -h 1080")]
    [InlineData("unity", "-screen-width 1920 -screen-height 1080")]
    [InlineData("unreal", "-ResX=1920 -ResY=1080")]
    public void Resolucao_no_estilo_de_cada_engine(string style, string expected) => Assert.Equal(expected, Args(style, res: "1920x1080"));

    [Theory]
    [InlineData("default")]
    [InlineData("")]
    [InlineData("1920")]
    [InlineData("1920x")]
    [InlineData("1920x1080; calc.exe")]
    public void Resolucao_invalida_e_ignorada(string res) => Assert.Equal("", Args("source", res: res));

    [Fact]
    public void Source_envia_as_duas_opcoes_de_taxa() => Assert.Equal("-refresh 240 -freq 240", Args("source", hz: 240));

    [Theory]
    [InlineData("unity")]
    [InlineData("unreal")]
    public void Outras_engines_nao_recebem_taxa(string style) => Assert.Equal("", Args(style, hz: 240));

    [Fact]
    public void Sem_taxa_nao_envia_nada() => Assert.Equal("", Args("source", hz: 0));

    [Fact]
    public void Tudo_junto_em_ordem() =>
        Assert.Equal("-windowed -noborder -w 2560 -h 1440 -refresh 165 -freq 165 -novid",
                     Args("source", "borderless", "2560x1440", 165, "  -novid  "));

    [Fact]
    public void Estilo_desconhecido_usa_o_padrao_source() => Assert.Equal("-fullscreen", Args("outro", "full"));
}
