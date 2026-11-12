using System.IO;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

/// <summary>Conferir e executar sem intervalo: o arquivo fica travado da conferência até o processo nascer.</summary>
public sealed class VerifiedLaunchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("thg-verified-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Make(string name, string content = "instalador original")
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Conferido_e_iniciado_devolve_o_resultado_do_start_e_mantem_o_arquivo()
    {
        string p = Make("ok.exe");
        int r = VerifiedLaunch.Run(p, _ => { }, () => 42);
        Assert.Equal(42, r);
        Assert.True(File.Exists(p));
    }

    [Fact]
    public void Conferencia_recusada_nao_inicia_e_apaga_o_arquivo()
    {
        string p = Make("ruim.exe");
        bool started = false;
        var ex = Assert.Throws<LaunchRefusedException>(() =>
            VerifiedLaunch.Run(p, _ => throw new InvalidOperationException("Assinatura digital inválida (0x80096010)."), () => started = true));
        Assert.False(ex.Locked);
        Assert.Contains("Assinatura digital inválida", ex.Message);
        Assert.False(started);
        Assert.False(File.Exists(p)); // o temporário recusado não fica no disco
    }

    [Fact]
    public void Conferencia_recusada_pode_preservar_o_arquivo_quando_quem_chama_pede()
    {
        string p = Make("ruim.exe");
        Assert.Throws<LaunchRefusedException>(() => VerifiedLaunch.Run(p, _ => throw new InvalidDataException("x"), () => 0, deleteRefused: false));
        Assert.True(File.Exists(p));
    }

    [Fact]
    public void Falha_ao_iniciar_nao_e_confundida_com_recusa_e_nao_apaga_o_arquivo()
    {
        string p = Make("ok.exe");
        Assert.Throws<System.ComponentModel.Win32Exception>(() => VerifiedLaunch.Run<int>(p, _ => { }, () => throw new System.ComponentModel.Win32Exception(5)));
        Assert.True(File.Exists(p));
    }

    [Fact]
    public void Arquivo_que_sumiu_e_recusa_por_trava_sem_conferir_nem_iniciar()
    {
        bool touched = false;
        var ex = Assert.Throws<LaunchRefusedException>(() =>
            VerifiedLaunch.Run(Path.Combine(_dir, "nao-existe.exe"), _ => touched = true, () => touched = true));
        Assert.True(ex.Locked);
        Assert.False(touched);
    }

    [Fact]
    public void Arquivo_aberto_para_escrita_por_outro_nao_e_travado_nem_executado()
    {
        string p = Make("em-uso.exe");
        bool touched = false;
        using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<LaunchRefusedException>(() => VerifiedLaunch.Run(p, _ => touched = true, () => touched = true));
            Assert.True(ex.Locked);
        }
        Assert.False(touched);
        Assert.True(File.Exists(p)); // não foi conferido: não é apagado
    }

    [Fact]
    public void Durante_a_conferencia_e_o_start_ninguem_altera_apaga_ou_troca_o_arquivo()
    {
        string p = Make("travado.exe");
        string other = Make("outro.exe", "instalador trocado");
        // O Windows recusa com IOException (violação de compartilhamento) ou UnauthorizedAccessException (troca por cima), conforme a operação.
        static void Denied(Action op)
        {
            var ex = Record.Exception(op);
            Assert.True(ex is IOException or UnauthorizedAccessException, $"a operação devia ser recusada, e veio: {ex?.GetType().Name ?? "sucesso"}");
        }
        void AssertLocked()
        {
            Denied(() => { using var w = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.None); });
            if (!OperatingSystem.IsWindows()) return; // apagar e renomear arquivo aberto só são barrados pelo Windows
            Denied(() => File.Delete(p));
            Denied(() => File.Move(other, p, overwrite: true));
            Denied(() => File.Move(p, p + ".bak"));
            Assert.True(File.Exists(other)); // a troca não aconteceu
        }
        VerifiedLaunch.Run(p, _ => AssertLocked(), () => { AssertLocked(); return 0; });
        Assert.Equal("instalador original", File.ReadAllText(p));
        File.WriteAllText(p, "depois de solto pode"); // a trava não fica presa depois do start
    }

    [Fact]
    public void A_trava_deixa_o_proprio_instalador_ler_o_executavel()
    {
        string p = Make("le-a-si-mesmo.exe");
        string read = VerifiedLaunch.Run(p, _ => { }, () =>
        {
            using var r = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new StreamReader(r).ReadToEnd();
        });
        Assert.Equal("instalador original", read);
    }

    [Fact]
    public void CheckSha256_le_pela_propria_trava_e_recusa_conteudo_diferente()
    {
        string p = Make("hash.exe");
        string sha = BinaryTrust.Sha256(p);
        VerifiedLaunch.Run(p, hold => { hold.Position = 7; VerifiedLaunch.CheckSha256(hold, sha.ToLowerInvariant(), "mudou"); }, () => 0);
        File.WriteAllText(p, "instalador alterado");
        var ex = Assert.Throws<LaunchRefusedException>(() => VerifiedLaunch.Run(p, hold => VerifiedLaunch.CheckSha256(hold, sha, "mudou"), () => 0));
        Assert.Equal("mudou", ex.Message);
    }
}

/// <summary>Instalador de driver guardado: o que abre é exatamente o que foi baixado e conferido.</summary>
[Collection("costuras-de-seguranca")]
public sealed class PendingDriverLaunchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("thg-pending-").FullName;
    private readonly Action<string, string> _check = DriverInstallers.PublisherCheck;
    private readonly Action<string> _starter = DriverInstallers.Starter;
    private readonly List<string> _started = new();

    public PendingDriverLaunchTests()
    {
        DriverInstallers.PublisherCheck = (_, _) => { };
        DriverInstallers.Starter = _started.Add;
    }

    public void Dispose()
    {
        DriverInstallers.PublisherCheck = _check;
        DriverInstallers.Starter = _starter;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private PendingDriver Pending(string vendor = "NVIDIA", bool withSha = true)
    {
        string p = Path.Combine(_dir, "driver.exe");
        File.WriteAllText(p, "driver baixado");
        return new PendingDriver { Path = p, Vendor = vendor, Sha256 = withSha ? BinaryTrust.Sha256(p) : "" };
    }

    [Fact]
    public void Instalador_integro_e_do_fabricante_abre()
    {
        var p = Pending();
        Assert.Equal(("", false), DriverInstallers.LaunchVerified(p));
        Assert.Equal(new[] { p.Path }, _started);
        Assert.True(File.Exists(p.Path));
    }

    [Fact]
    public void Arquivo_alterado_depois_do_download_nao_abre_e_e_descartado()
    {
        var p = Pending();
        File.WriteAllText(p.Path, "driver adulterado");
        var (error, discarded) = DriverInstallers.LaunchVerified(p);
        Assert.Contains("SHA-256 diferente", error);
        Assert.Contains("Baixe de novo", error);
        Assert.True(discarded);
        Assert.Empty(_started);
        Assert.False(File.Exists(p.Path));
    }

    [Fact]
    public void Arquivo_substituido_por_outro_antes_da_conferencia_nao_abre()
    {
        var p = Pending();
        string other = Path.Combine(_dir, "outro.exe");
        File.WriteAllText(other, "outro programa");
        File.Move(other, p.Path, overwrite: true);
        Assert.True(DriverInstallers.LaunchVerified(p).Discarded);
        Assert.Empty(_started);
    }

    [Theory]
    [InlineData("NVIDIA", "NVIDIA")]
    [InlineData("AMD", "Advanced Micro Devices")]
    public void Assinatura_de_outro_fabricante_ou_invalida_nao_abre(string vendor, string expectedPublisher)
    {
        var p = Pending(vendor);
        string? asked = null;
        DriverInstallers.PublisherCheck = (_, publisher) => { asked = publisher; throw new InvalidOperationException("Assinatura digital inválida (0x800B0109). O arquivo foi descartado."); };
        var (error, discarded) = DriverInstallers.LaunchVerified(p);
        Assert.Equal(expectedPublisher, asked);
        Assert.Contains("Assinatura digital inválida", error);
        Assert.True(discarded);
        Assert.Empty(_started);
        Assert.False(File.Exists(p.Path));
    }

    [Fact]
    public void Instalador_antigo_sem_sha_guardado_ainda_exige_a_assinatura_do_fabricante()
    {
        var p = Pending(withSha: false);
        int checks = 0;
        DriverInstallers.PublisherCheck = (_, _) => checks++;
        Assert.Equal("", DriverInstallers.LaunchVerified(p).Error);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void A_conferencia_e_a_abertura_acontecem_com_o_arquivo_travado()
    {
        var p = Pending();
        bool lockedAtCheck = false, lockedAtStart = false;
        bool Locked()
        {
            try { using var w = new FileStream(p.Path, FileMode.Open, FileAccess.Write, FileShare.None); return false; }
            catch (IOException) { return true; }
        }
        DriverInstallers.PublisherCheck = (_, _) => lockedAtCheck = Locked();
        DriverInstallers.Starter = _ => lockedAtStart = Locked();
        Assert.Equal("", DriverInstallers.LaunchVerified(p).Error);
        Assert.True(lockedAtCheck);
        Assert.True(lockedAtStart);
    }

    [Fact]
    public void Sem_conseguir_a_trava_nao_abre_e_nao_apaga_o_instalador()
    {
        var p = Pending();
        using (new FileStream(p.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (error, discarded) = DriverInstallers.LaunchVerified(p);
            Assert.Contains("travar", error);
            Assert.False(discarded);
        }
        Assert.Empty(_started);
        Assert.True(File.Exists(p.Path));
    }

    [Fact]
    public void Falha_do_Windows_ao_abrir_e_informada_e_o_instalador_fica()
    {
        var p = Pending();
        DriverInstallers.Starter = _ => throw new System.ComponentModel.Win32Exception(1223, "A operação foi cancelada pelo usuário.");
        var (error, discarded) = DriverInstallers.LaunchVerified(p);
        Assert.Contains("cancelada", error);
        Assert.False(discarded);
        Assert.True(File.Exists(p.Path));
    }
}

/// <summary>Relatório de falha: página curta pelo Explorer do usuário, texto na área de transferência, sem dados pessoais.</summary>
public class CrashReportTests
{
    private const string Trace = "System.IO.IOException: acesso negado a C:\\Users\\Maria Silva\\AppData\\Roaming\\x.json\r\n" +
                                 "   em ThgOptimization.AppSettings.Save() · PC DESKTOP-MARIA · usuária Maria Silva";

    [Fact]
    public void O_endereco_e_curto_passa_pela_politica_de_links_e_nao_leva_o_texto_da_falha()
    {
        var r = CrashReporter.Prepare("dono/repo", "1.9.0", new string('x', 200_000));
        Assert.True(Unelevated.IsWebUrl(r.Url));
        Assert.True(Unelevated.IsShellTarget(r.Url)); // cabe no Explorer: não precisa de abertura elevada
        Assert.True(r.Url.Length < 600);
        Assert.StartsWith("https://github.com/dono/repo/issues/new?title=", r.Url);
        Assert.DoesNotContain("xxxx", r.Url);
        Assert.Contains(Uri.EscapeDataString("Falha no tHg 1.9.0"), r.Url);
    }

    [Fact]
    public void O_texto_copiado_traz_a_falha_inteira_ate_o_teto_e_avisa_quando_corta()
    {
        var r = CrashReporter.Prepare("dono/repo", "1.9.0", "NullReferenceException em Foo.Bar()");
        Assert.Equal("```\nNullReferenceException em Foo.Bar()\n```", r.Text);
        var big = CrashReporter.Prepare("dono/repo", "1.9.0", new string('y', CrashReporter.MaxReportChars + 500));
        Assert.Contains("…(cortado)", big.Text);
        Assert.True(big.Text.Length < 65_536); // limite do GitHub para o corpo de uma issue
    }

    [Fact]
    public void Sanitize_tira_perfil_usuario_e_nome_do_pc()
    {
        string clean = CrashReporter.Sanitize(Trace, "Maria Silva", @"C:\Users\Maria Silva", "DESKTOP-MARIA");
        Assert.DoesNotContain("Maria", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DESKTOP-MARIA", clean);
        Assert.Contains(@"%PERFIL%\AppData\Roaming\x.json", clean);
        Assert.Contains("usuária %USUARIO%", clean);
        Assert.Contains("PC %PC%", clean);
        Assert.Contains("ThgOptimization.AppSettings.Save()", clean); // o diagnóstico continua lá
    }

    [Fact]
    public void Sanitize_ignora_maiusculas_e_nao_estraga_o_texto_com_nomes_curtos()
    {
        Assert.Equal(@"%PERFIL%\a.txt", CrashReporter.Sanitize(@"c:\users\ana\a.txt", "Ana", @"C:\Users\Ana", null));
        // Usuário "eu": trocar solto mexeria em "seu", "meu"... A pasta do perfil ainda é trocada.
        Assert.Equal(@"seu arquivo em %PERFIL%\x", CrashReporter.Sanitize(@"seu arquivo em C:\Users\eu\x", "eu", @"C:\Users\eu", "PC"));
        Assert.Equal("sem nada", CrashReporter.Sanitize("sem nada", null, null, null));
    }
}

/// <summary>Tique da janela: uma etapa que falha não derruba o app nem as outras, e falha permanente não fica escondida.</summary>
public class TickGuardTests
{
    private sealed class Sink
    {
        public readonly List<string> Logged = new(), Persistent = new();
        public TickGuard Guard() => new("teste", (step, _) => Persistent.Add(step), (step, ex) => Logged.Add($"{step}|{ex.Message}"));
    }

    [Fact]
    public void Etapa_que_falha_nao_impede_as_seguintes()
    {
        var s = new Sink(); var g = s.Guard();
        var ran = new List<string>();
        Assert.True(g.Step("a", () => ran.Add("a")));
        Assert.False(g.Step("b", () => throw new InvalidOperationException("sensor sumiu")));
        Assert.True(g.Step("c", () => ran.Add("c")));
        Assert.Equal(new[] { "a", "c" }, ran);
        Assert.Equal(new[] { "b|sensor sumiu" }, s.Logged);
        Assert.Equal(1, g.Failures("b"));
        Assert.Equal(0, g.Failures("a"));
    }

    [Fact]
    public async Task Etapa_assincrona_que_falha_depois_do_await_tambem_e_contida()
    {
        var s = new Sink(); var g = s.Guard();
        bool ok = await g.StepAsync("sensores", async () => { await Task.Yield(); throw new TimeoutException("driver travado"); });
        Assert.False(ok);
        Assert.True(await g.StepAsync("sensores", () => Task.CompletedTask));
        Assert.Equal(0, g.Failures("sensores")); // voltou a funcionar: a contagem zera
        Assert.Single(s.Logged);
    }

    [Fact]
    public void Falha_permanente_registra_a_primeira_avisa_uma_vez_e_volta_ao_registro_periodicamente()
    {
        var s = new Sink(); var g = s.Guard();
        for (int i = 0; i < TickGuard.PersistentAfter * 2; i++) g.Step("gpu", () => throw new InvalidOperationException("x"));
        Assert.Equal(new[] { "gpu" }, s.Persistent);              // um aviso na tela, não um por segundo
        Assert.Equal(3, s.Logged.Count);                           // 1ª, 30ª e 60ª: nem silêncio, nem enchente
        Assert.Contains("falha 30 seguida", s.Logged[1]);
        Assert.Equal(TickGuard.PersistentAfter * 2, g.Failures("gpu"));
    }

    [Fact]
    public void Depois_de_se_recuperar_uma_nova_sequencia_de_falhas_avisa_de_novo()
    {
        var s = new Sink(); var g = s.Guard();
        for (int i = 0; i < TickGuard.PersistentAfter; i++) g.Step("gpu", () => throw new InvalidOperationException("x"));
        g.Step("gpu", () => { });
        for (int i = 0; i < TickGuard.PersistentAfter; i++) g.Step("gpu", () => throw new InvalidOperationException("x"));
        Assert.Equal(2, s.Persistent.Count);
    }

    [Fact]
    public void Aviso_ou_registro_que_falham_nao_derrubam_o_tique()
    {
        var g = new TickGuard("teste", (_, _) => throw new InvalidOperationException("janela fechada"), (_, _) => throw new IOException("disco cheio"));
        for (int i = 0; i < TickGuard.PersistentAfter; i++) Assert.False(g.Step("x", () => throw new InvalidOperationException("y")));
    }

    [Fact]
    public void As_contagens_sao_por_etapa()
    {
        var s = new Sink(); var g = s.Guard();
        g.Step("a", () => throw new InvalidOperationException("1"));
        g.Step("b", () => throw new InvalidOperationException("2"));
        g.Step("a", () => throw new InvalidOperationException("3"));
        Assert.Equal(2, g.Failures("a"));
        Assert.Equal(1, g.Failures("b"));
        Assert.Equal(2, s.Logged.Count); // a 2ª falha seguida de "a" não repete o registro
    }
}
