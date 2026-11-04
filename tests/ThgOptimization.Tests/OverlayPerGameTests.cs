using System.Text.Json;
using ThgOptimization.Core.Performance;

namespace ThgOptimization.Tests;

public class OverlayPerGameTests
{
    private static readonly HashSet<string> Global = new() { "fps", "low1" };

    [Fact]
    public void Jogo_sem_overlay_nao_muda_global()
    {
        Assert.Same(Global, OverlayPerGame.Items(Global, null));
        Assert.Same(Global, OverlayPerGame.Items(Global, new GameProfile()));
        Assert.Equal("TL", OverlayPerGame.Corner("TL", new GameProfile()));
        Assert.False(OverlayPerGame.HasOverride(new GameProfile()));
    }

    [Fact]
    public void Jogo_com_overlay_aplica_e_restaura()
    {
        var profile = new GameProfile();
        OverlayPerGame.Use(profile, new[] { "fps" }, "BR");
        Assert.Equal(new[] { "fps" }, OverlayPerGame.Items(Global, profile));
        Assert.Equal("BR", OverlayPerGame.Corner("TL", profile));
        Assert.True(OverlayPerGame.HasOverride(profile));
        Assert.Equal(new[] { "fps", "low1" }, Global); // a escolha global nunca é tocada: jogo fechado (sem perfil) volta a ela

        OverlayPerGame.Clear(profile);
        Assert.Same(Global, OverlayPerGame.Items(Global, profile));
        Assert.False(OverlayPerGame.HasOverride(profile));
    }

    [Fact]
    public void OverlayItems_null_usa_global()
    {
        var profile = new GameProfile { OverlayItems = null, OverlayCorner = "BL" };
        Assert.Same(Global, OverlayPerGame.Items(Global, profile));
        Assert.Equal("BL", OverlayPerGame.Corner("TL", profile));
    }

    [Fact]
    public void Lista_vazia_e_uma_escolha_e_nao_o_padrao()
    {
        Assert.Empty(OverlayPerGame.Items(Global, new GameProfile { OverlayItems = new() }));
    }

    [Fact]
    public void Copia_para_gravar_nao_divide_a_lista_e_o_json_volta_igual()
    {
        var s = new AppSettings();
        s.GameProfiles["a"] = new GameProfile { OverlayItems = new() { "fps" }, OverlayCorner = "BR" };
        var copy = s.Snapshot();
        s.GameProfiles["a"].OverlayItems!.Clear();
        Assert.Equal(new[] { "fps" }, copy.GameProfiles["a"].OverlayItems);

        var back = JsonSerializer.Deserialize<AppSettings>(copy.SerializeSnapshot())!;
        Assert.Equal(new[] { "fps" }, back.GameProfiles["a"].OverlayItems);
        Assert.Equal("BR", back.GameProfiles["a"].OverlayCorner);
    }
}
