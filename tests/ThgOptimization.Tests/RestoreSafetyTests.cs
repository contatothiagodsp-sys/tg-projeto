namespace ThgOptimization.Tests;

/// <summary>
/// "Desfazer": a cópia do valor original só é apagada quando a restauração deu certo. Antes, a falha era engolida, a cópia
/// era apagada do mesmo jeito e a tela dizia "Restaurado" — o valor original se perdia.
/// </summary>
public sealed class RestoreSafetyTests
{
    private static Tweak Ok(string id, List<string> order) => new() { Id = id, Title = "Ajuste " + id, Restore = _ => order.Add(id) };
    private static Tweak Broken(string id) => new() { Id = id, Title = "Ajuste " + id, Restore = _ => throw new UnauthorizedAccessException("recusado pelo Windows") };

    [Fact]
    public void Tudo_restaurado_apaga_as_copias_e_preserva_as_de_outras_paginas()
    {
        var order = new List<string>();
        var s = new AppSettings();
        s.TweakBackup["a"] = "1"; s.TweakBackup["b.x"] = "2"; s.TweakBackup["sec.lsa"] = "d:0"; s.TweakBackup["nv.perfil"] = "x";

        var failed = WindowsTweaks.RestoreAllCore(new[] { Ok("a", order), Ok("b", order) }, s, _ => { });

        Assert.Empty(failed);
        Assert.Equal(new[] { "b", "a" }, order); // ordem inversa
        Assert.Equal(new[] { "nv.perfil", "sec.lsa" }, s.TweakBackup.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Falha_em_um_ajuste_preserva_as_copias_e_diz_qual_falhou()
    {
        var order = new List<string>();
        var s = new AppSettings();
        s.TweakBackup["a"] = "1"; s.TweakBackup["b"] = "2";

        var failed = WindowsTweaks.RestoreAllCore(new[] { Ok("a", order), Broken("b") }, s, _ => { });

        Assert.Equal(new[] { "Ajuste b" }, failed);
        Assert.Equal(new[] { "a" }, order);               // a falha de um não impede os outros
        Assert.Equal("2", s.TweakBackup["b"]);            // o valor original continua guardado
        Assert.True(s.TweakBackup.ContainsKey("a"));      // restaurar de novo é seguro: regrava o mesmo valor
    }

    [Fact]
    public void Monitor_que_recusou_a_taxa_fica_com_a_copia_e_aparece_como_falha()
    {
        var s = new AppSettings();
        s.TweakBackup["a"] = "1"; s.TweakBackup[@"display.\\.\DISPLAY1"] = "60";

        // DisplayTuner tira a cópia só quando o monitor aceita: aqui ele recusou e deixou a cópia.
        var failed = WindowsTweaks.RestoreAllCore(new[] { Ok("a", new()) }, s, _ => { });

        Assert.Equal(new[] { "Taxa de atualização dos monitores" }, failed);
        Assert.Equal(new[] { @"display.\\.\DISPLAY1" }, s.TweakBackup.Keys); // os ajustes que voltaram saem; a do monitor fica
    }

    [Fact]
    public void Excecao_na_restauracao_dos_monitores_nao_apaga_a_copia_deles()
    {
        var s = new AppSettings();
        s.TweakBackup["display.X"] = "144";

        var failed = WindowsTweaks.RestoreAllCore(Array.Empty<Tweak>(), s, _ => throw new InvalidOperationException("driver"));

        Assert.Single(failed);
        Assert.Equal("144", s.TweakBackup["display.X"]);
    }

    [Fact]
    public void Limpeza_so_apaga_as_copias_quando_tudo_voltou()
    {
        var s = new AppSettings();
        s.TweakBackup["clean.widgets"] = "d:1"; s.TweakBackup["a"] = "1";

        Assert.NotEmpty(WindowsTweaks.RestoreCleanupCore(new[] { Broken("clean.widgets") }, s));
        Assert.True(s.TweakBackup.ContainsKey("clean.widgets"));

        Assert.Empty(WindowsTweaks.RestoreCleanupCore(new[] { Ok("clean.widgets", new()) }, s));
        Assert.Equal(new[] { "a" }, s.TweakBackup.Keys); // só as desta página
    }

    [Fact]
    public void SHIELD_tira_a_copia_do_que_voltou_e_guarda_a_do_que_falhou()
    {
        var s = new AppSettings();
        s.TweakBackup["sec.lsa"] = "d:0"; s.TweakBackup["sec.firewall.publicprofile"] = "off"; s.TweakBackup["sec.pua"] = "0";
        s.TweakBackup["hags"] = "1";

        var failed = SecurityAudit.RestoreAllCore(s, (key, _) => key switch
        {
            "sec.lsa" => null,                                   // voltou
            "sec.pua" => "o Defender recusou",                   // o Windows respondeu com erro
            _ => throw new UnauthorizedAccessException("negado"), // exceção
        });

        Assert.Equal(new[] { "Firewall do Windows", "Proteção contra aplicativos indesejados" }, failed.OrderBy(f => f, StringComparer.Ordinal));
        Assert.Equal(new[] { "hags", "sec.firewall.publicprofile", "sec.pua" }, s.TweakBackup.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void SHIELD_nao_repete_o_mesmo_item_na_mensagem()
    {
        var s = new AppSettings();
        s.TweakBackup["sec.firewall.publicprofile"] = "off"; s.TweakBackup["sec.firewall.privateprofile"] = "off";
        Assert.Equal(new[] { "Firewall do Windows" }, SecurityAudit.RestoreAllCore(s, (_, _) => "falhou"));
    }

    [Fact]
    public void A_mensagem_diz_o_que_falhou_e_que_da_para_tentar_de_novo()
    {
        string msg = WindowsTweaks.DescribeRestoreFailure(new[] { "A", "B", "C", "D", "E", "F" });
        Assert.Contains("A; B; C; D", msg);
        Assert.Contains("mais 2", msg);
        Assert.Contains("continuam guardados", msg);
    }

    [Fact]
    public void Nenhuma_restauracao_engole_a_falha_e_apaga_a_copia()
    {
        // Guarda de regressão do padrão antigo: try { t.Restore(s); } catch { } seguido de TweakBackup.Remove.
        foreach (string file in new[] { "WindowsTweaks.cs", "WindowsTweaks.Competitive.cs" })
            Assert.DoesNotContain("try { t.Restore(s); } catch { }", RepoText.File(file));
    }
}
