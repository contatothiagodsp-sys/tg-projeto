using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

/// <summary>As costuras de teste são estáticas: estas classes não rodam em paralelo entre si.</summary>
[CollectionDefinition("costuras-de-seguranca", DisableParallelization = true)]
public sealed class SecuritySeamsCollection { }

/// <summary>Abrir sem administrador: ou abre com o token do Explorer, ou não abre. Nunca cai para uma abertura elevada.</summary>
[Collection("costuras-de-seguranca")]
public sealed class UnelevatedTests : IDisposable
{
    private readonly Func<string, string, string?, Process?> _original = Unelevated.ShellTokenStarter;
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"thg-unelevated-{Guid.NewGuid():N}.log");

    public UnelevatedTests() => AppLog.UseFile(_log);

    public void Dispose()
    {
        Unelevated.ShellTokenStarter = _original;
        try { File.Delete(_log); } catch { }
    }

    [Fact]
    public void Sucesso_devolve_o_processo_criado_com_o_token_do_shell()
    {
        using var me = Process.GetCurrentProcess();
        (string Exe, string Args, string? Dir)? seen = null;
        Unelevated.ShellTokenStarter = (exe, args, dir) => { seen = (exe, args, dir); return me; };

        var r = Unelevated.TryStart(@"C:\Jogos\jogo.exe", "-fullscreen", @"C:\Jogos");

        Assert.True(r.Started);
        Assert.Same(me, r.Process);
        Assert.Null(r.Error);
        Assert.Equal((@"C:\Jogos\jogo.exe", "-fullscreen", @"C:\Jogos"), seen);
    }

    [Fact]
    public void Processo_que_abriu_e_ja_saiu_ainda_conta_como_aberto()
    {
        Unelevated.ShellTokenStarter = (_, _, _) => null; // launcher que entrega para outro processo e sai

        var r = Unelevated.TryStart(@"C:\Jogos\launcher.exe", "", null);

        Assert.True(r.Started);
        Assert.Null(r.Process);
    }

    [Fact]
    public void Falha_do_token_nao_abre_nada_e_devolve_o_motivo()
    {
        int calls = 0;
        Unelevated.ShellTokenStarter = (_, _, _) => { calls++; throw new Win32Exception(5, "Acesso negado."); };

        var r = Unelevated.TryStart(@"C:\Jogos\jogo.exe", "", null);

        Assert.False(r.Started);
        Assert.Null(r.Process); // nenhum processo: não existe abertura "do jeito normal", que herdaria administrador
        Assert.Equal(1, calls);
        Assert.Contains("sem privilégio de administrador", r.Error);
        Assert.Contains("Acesso negado", r.Error);
        Assert.Contains("pelo Windows", r.Error); // o usuário continua podendo abrir por conta própria
    }

    [Fact]
    public void Sem_Explorer_explica_o_que_fazer()
    {
        Unelevated.ShellTokenStarter = (_, _, _) => throw new Unelevated.ShellUnavailableException();

        var r = Unelevated.TryStart(@"C:\Jogos\jogo.exe", "", null);

        Assert.False(r.Started);
        Assert.Contains("Explorer", r.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_programa_falha_sem_tentar(string exe)
    {
        Unelevated.ShellTokenStarter = (_, _, _) => throw new Xunit.Sdk.XunitException("não devia tentar abrir");
        Assert.False(Unelevated.TryStart(exe, "", null).Started);
    }

    [Fact]
    public void O_registro_guarda_so_o_nome_do_arquivo_sem_caminho_nem_argumentos()
    {
        Unelevated.ShellTokenStarter = (_, _, _) => throw new InvalidOperationException("falhou");

        Unelevated.TryStart(@"C:\Users\fulano\Jogos\jogo.exe", "--token=SEGREDO-123", @"C:\Users\fulano\Jogos");

        string log = File.ReadAllText(_log);
        Assert.Contains("jogo.exe", log);
        Assert.DoesNotContain("fulano", log);
        Assert.DoesNotContain("SEGREDO", log);
    }

    [Fact]
    public void Nao_existe_mais_caminho_que_abra_com_ShellExecute()
    {
        // Guarda de regressão: o antigo "catch { Process.Start(UseShellExecute = true) }" dava administrador ao programa.
        string src = RepoText.File("Unelevated.cs");
        Assert.DoesNotContain("UseShellExecute", src);
        Assert.DoesNotContain("Process.Start(", src);
    }
}

/// <summary>Assinatura digital: sem assinatura, válida, inválida e impossível de verificar são quatro situações diferentes.</summary>
[Collection("costuras-de-seguranca")]
public sealed class BinaryTrustTests : IDisposable
{
    private const int NoSignature = unchecked((int)0x800B0100), BadDigest = unchecked((int)0x80096010),
                      UntrustedRoot = unchecked((int)0x800B0109), Expired = unchecked((int)0x800B0101),
                      AccessDenied = unchecked((int)0x80070005), Fail = unchecked((int)0x80004005);

    private readonly Func<string, int> _trust = BinaryTrust.TrustProbe;
    private readonly Func<string, string> _subject = BinaryTrust.SubjectProbe;
    private readonly string _dir = Directory.CreateTempSubdirectory("thg-binarytrust-").FullName;

    public void Dispose()
    {
        BinaryTrust.TrustProbe = _trust;
        BinaryTrust.SubjectProbe = _subject;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SignatureInfo Valid(string subject) => new(SignatureState.Valid, subject, 0);
    private static readonly SignatureInfo Unsigned = new(SignatureState.Unsigned, null, NoSignature);

    [Theory]
    [InlineData(0, SignatureState.Valid)]
    [InlineData(NoSignature, SignatureState.Unsigned)]
    [InlineData(unchecked((int)0x800B0003), SignatureState.Unsigned)]
    [InlineData(BadDigest, SignatureState.Invalid)]
    [InlineData(UntrustedRoot, SignatureState.Invalid)]
    [InlineData(Expired, SignatureState.Invalid)]
    [InlineData(unchecked((int)0x800B010C), SignatureState.Invalid)] // revogado
    [InlineData(AccessDenied, SignatureState.Unverifiable)]
    [InlineData(Fail, SignatureState.Unverifiable)]
    [InlineData(unchecked((int)0x8007000E), SignatureState.Unverifiable)] // sem memória
    public void Classify_separa_as_quatro_situacoes(int code, SignatureState expected) =>
        Assert.Equal(expected, BinaryTrust.Classify(code));

    [Fact]
    public void Inspect_falha_tecnica_nao_vira_sem_assinatura()
    {
        BinaryTrust.TrustProbe = _ => throw new DllNotFoundException("wintrust.dll");
        var info = BinaryTrust.Inspect("qualquer.exe");
        Assert.Equal(SignatureState.Unverifiable, info.State);
        Assert.Contains("wintrust", info.Detail);
    }

    [Fact]
    public void Inspect_assinatura_aceita_mas_certificado_ilegivel_e_nao_verificavel()
    {
        BinaryTrust.TrustProbe = _ => 0;
        BinaryTrust.SubjectProbe = _ => throw new System.Security.Cryptography.CryptographicException("certificado ilegível");
        Assert.Equal(SignatureState.Unverifiable, BinaryTrust.Inspect("x.exe").State);
    }

    [Fact]
    public void Inspect_valida_traz_o_titular()
    {
        BinaryTrust.TrustProbe = _ => 0;
        BinaryTrust.SubjectProbe = _ => "CN=tHg";
        Assert.Equal(Valid("CN=tHg"), BinaryTrust.Inspect("x.exe"));
    }

    [Fact]
    public void Signer_mantem_o_contrato_null_sem_assinatura_e_excecao_no_resto()
    {
        BinaryTrust.SubjectProbe = _ => "CN=tHg";
        BinaryTrust.TrustProbe = _ => NoSignature;
        Assert.Null(BinaryTrust.Signer("x.exe"));
        BinaryTrust.TrustProbe = _ => 0;
        Assert.Equal("CN=tHg", BinaryTrust.Signer("x.exe"));
        BinaryTrust.TrustProbe = _ => BadDigest;
        Assert.Contains("inválida", Assert.Throws<InvalidDataException>(() => BinaryTrust.Signer("x.exe")).Message);
        BinaryTrust.TrustProbe = _ => AccessDenied;
        Assert.Contains("verificar", Assert.Throws<InvalidDataException>(() => BinaryTrust.Signer("x.exe")).Message);
    }

    // ---- decisão da atualização ----

    [Fact]
    public void Build_local_sem_assinatura_aceita_instalador_sem_assinatura_ou_assinado() // vale o SHA-256
    {
        BinaryTrust.CheckSameSigner(Unsigned, Unsigned);
        BinaryTrust.CheckSameSigner(Unsigned, Valid("CN=tHg"));
    }

    [Fact]
    public void App_assinado_aceita_so_o_mesmo_emissor()
    {
        BinaryTrust.CheckSameSigner(Valid("CN=tHg"), Valid("CN=tHg"));
        Assert.Contains("outro emissor", Assert.Throws<InvalidDataException>(() => BinaryTrust.CheckSameSigner(Valid("CN=tHg"), Valid("CN=Outro"))).Message);
        Assert.Contains("não tem assinatura", Assert.Throws<InvalidDataException>(() => BinaryTrust.CheckSameSigner(Valid("CN=tHg"), Unsigned)).Message);
    }

    [Theory]
    [InlineData(SignatureState.Invalid)]
    [InlineData(SignatureState.Unverifiable)]
    public void Falha_ao_conferir_o_proprio_app_recusa_a_atualizacao(SignatureState mine)
    {
        // O defeito corrigido: esta falha era engolida e o app passava a se comportar como build sem assinatura.
        var self = new SignatureInfo(mine, null, mine == SignatureState.Invalid ? BadDigest : AccessDenied);
        var ex = Assert.Throws<InvalidDataException>(() => BinaryTrust.CheckSameSigner(self, Valid("CN=Qualquer")));
        Assert.Contains("deste tHg", ex.Message);
        Assert.Throws<InvalidDataException>(() => BinaryTrust.CheckSameSigner(self, Unsigned));
    }

    [Theory]
    [InlineData(SignatureState.Invalid)]
    [InlineData(SignatureState.Unverifiable)]
    public void Instalador_com_assinatura_ruim_e_recusado_ate_em_build_local(SignatureState theirs)
    {
        var installer = new SignatureInfo(theirs, null, BadDigest);
        Assert.Throws<InvalidDataException>(() => BinaryTrust.CheckSameSigner(Unsigned, installer));
        Assert.Throws<InvalidDataException>(() => BinaryTrust.CheckSameSigner(Valid("CN=tHg"), installer));
    }

    // ---- VerifyUpdate de ponta a ponta, com arquivos reais e o Windows simulado ----

    private string Installer(out string sha)
    {
        string p = Path.Combine(_dir, "setup.exe");
        File.WriteAllBytes(p, new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 });
        sha = BinaryTrust.Sha256(p);
        return p;
    }

    [Fact]
    public void VerifyUpdate_recusa_sha_diferente_antes_de_olhar_a_assinatura()
    {
        string p = Installer(out _);
        BinaryTrust.TrustProbe = _ => throw new Xunit.Sdk.XunitException("não devia chegar na assinatura");
        var ex = Assert.Throws<InvalidDataException>(() => BinaryTrust.VerifyUpdate(p, new string('0', 64), "self.exe"));
        Assert.Contains("SHA-256", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void VerifyUpdate_recusa_sha_ausente_ou_malformado(string sha)
    {
        string p = Installer(out _);
        Assert.Throws<InvalidDataException>(() => BinaryTrust.VerifyUpdate(p, sha, "self.exe"));
    }

    [Fact]
    public void VerifyUpdate_app_cuja_assinatura_nao_pode_ser_conferida_recusa()
    {
        string p = Installer(out string sha);
        BinaryTrust.SubjectProbe = _ => "CN=tHg";
        BinaryTrust.TrustProbe = path => path == "self.exe" ? AccessDenied : 0;
        var ex = Assert.Throws<InvalidDataException>(() => BinaryTrust.VerifyUpdate(p, sha, "self.exe"));
        Assert.Contains("deste tHg", ex.Message);
    }

    [Fact]
    public void VerifyUpdate_sem_caminho_do_proprio_app_recusa()
    {
        string p = Installer(out string sha);
        BinaryTrust.TrustProbe = _ => NoSignature;
        Assert.Throws<InvalidDataException>(() => BinaryTrust.VerifyUpdate(p, sha, null));
    }

    [Fact]
    public void VerifyUpdate_build_local_com_sha_certo_passa()
    {
        string p = Installer(out string sha);
        BinaryTrust.TrustProbe = _ => NoSignature;
        BinaryTrust.VerifyUpdate(p, sha.ToLowerInvariant(), "self.exe");
    }

    [Fact]
    public void VerifyUpdate_mesmo_emissor_passa_e_emissor_diferente_nao()
    {
        string p = Installer(out string sha);
        BinaryTrust.TrustProbe = _ => 0;
        BinaryTrust.SubjectProbe = _ => "CN=tHg";
        BinaryTrust.VerifyUpdate(p, sha, "self.exe");
        BinaryTrust.SubjectProbe = path => path == "self.exe" ? "CN=tHg" : "CN=Outro";
        Assert.Throws<InvalidDataException>(() => BinaryTrust.VerifyUpdate(p, sha, "self.exe"));
    }
}

public sealed class AppUpdaterSafetyTests
{
    private static readonly string Sha = new('a', 64);
    private static UpdateInfo Info(string url, string? sha, long size = 1000) => new(new Version(9, 9, 9), url, sha, size, "");

    [Theory]
    [InlineData("https://github.com/dono/repo/releases/download/latest/tHgOptimization-Setup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/dono/repo/x.exe", false)]            // sem HTTPS
    [InlineData("https://github.com.evil.example/x.exe", false)]         // domínio parecido
    [InlineData("https://evilgithubusercontent.com/x.exe", false)]       // sufixo sem o ponto
    [InlineData("https://example.com/github.com/x.exe", false)]
    [InlineData("file:///C:/x.exe", false)]
    [InlineData("", false)]
    public void IsGitHub_so_aceita_https_dos_dominios_do_GitHub(string url, bool expected) =>
        Assert.Equal(expected, AppUpdater.IsGitHub(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc123")]
    public void Sem_sha256_valido_nem_comeca_a_baixar(string? sha)
    {
        var ex = Assert.Throws<InvalidDataException>(() => AppUpdater.EnsureDownloadable(Info("https://github.com/a/b/x.exe", sha)));
        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public void Endereco_fora_do_GitHub_nem_comeca_a_baixar() =>
        Assert.Throws<InvalidDataException>(() => AppUpdater.EnsureDownloadable(Info("https://example.com/x.exe", Sha)));

    [Fact]
    public void Com_sha_e_endereco_certos_pode_baixar() =>
        AppUpdater.EnsureDownloadable(Info("https://github.com/a/b/x.exe", Sha));

    [Fact]
    public async Task DownloadAsync_recusa_sem_sha_antes_de_tocar_no_disco_ou_na_rede()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppUpdater.DownloadAsync(Info("https://github.com/a/b/x.exe", null), new Progress<double>(), CancellationToken.None));
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(0, AppUpdater.MaxInstallerBytes)]
    [InlineData(-1, AppUpdater.MaxInstallerBytes)]
    [InlineData(long.MaxValue, AppUpdater.MaxInstallerBytes)]
    public void Limite_do_download_e_o_tamanho_publicado_com_teto(long published, long expected) =>
        Assert.Equal(expected, AppUpdater.DownloadLimit(published));

    private sealed class Sink : IProgress<double> { public double Last; public void Report(double v) => Last = v; }

    [Fact]
    public async Task Copia_dentro_do_limite_chega_inteira_e_o_progresso_fecha_em_100()
    {
        var data = new byte[200_000]; new Random(1).NextBytes(data);
        using var dst = new MemoryStream();
        var progress = new Sink();
        await AppUpdater.CopyLimitedAsync(new MemoryStream(data), dst, data.Length, data.Length, progress, CancellationToken.None);
        Assert.Equal(data, dst.ToArray());
        Assert.Equal(1.0, progress.Last);
    }

    [Fact]
    public async Task Servidor_que_manda_mais_do_que_o_publicado_e_interrompido()
    {
        using var dst = new MemoryStream();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppUpdater.CopyLimitedAsync(new MemoryStream(new byte[500_000]), dst, 100_000, 100_000, new Sink(), CancellationToken.None));
        Assert.Contains("maior que o publicado", ex.Message);
        Assert.True(dst.Length <= 100_000); // nada além do limite chega ao disco
    }

    [Fact]
    public async Task Cancelar_interrompe_a_copia()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppUpdater.CopyLimitedAsync(new MemoryStream(new byte[10]), new MemoryStream(), 10, 10, new Sink(), cts.Token));
    }

    [Fact]
    public void RunInstaller_recusa_arquivo_que_nao_confere_e_nao_executa()
    {
        string dir = Directory.CreateTempSubdirectory("thg-updater-").FullName;
        try
        {
            string file = Path.Combine(dir, "setup.exe");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            Assert.Throws<InvalidDataException>(() => AppUpdater.RunInstaller(file, Info("https://github.com/a/b/x.exe", Sha)));
            Assert.Throws<InvalidDataException>(() => AppUpdater.RunInstaller(file, Info("https://github.com/a/b/x.exe", null)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A premissa da trava do RunInstaller, conferida no Windows de verdade: com o arquivo aberto só para leitura compartilhada,
    /// ninguém consegue alterá-lo nem apagá-lo, e o Windows ainda consegue executá-lo.
    /// </summary>
    [Fact]
    public void Arquivo_travado_para_leitura_ainda_executa_e_nao_pode_ser_trocado()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Directory.CreateTempSubdirectory("thg-updater-lock-").FullName;
        try
        {
            string exe = Path.Combine(dir, "where-copia.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), exe);
            using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => File.WriteAllBytes(exe, new byte[] { 0 }));
                Assert.ThrowsAny<Exception>(() => File.Delete(exe));
                using var p = Process.Start(new ProcessStartInfo(exe, "/?") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                Assert.NotNull(p);
                Assert.True(p!.WaitForExit(15000));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
