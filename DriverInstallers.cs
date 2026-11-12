using System.Diagnostics;
using System.IO;

namespace ThgOptimization;

/// <summary>Instalador de driver baixado pelo app, lembrado entre reinícios (ex.: baixado antes da DDU).</summary>
public sealed class PendingDriver
{
    public string Path { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Version { get; set; } = "";
    public string Gpu { get; set; } = "";
    /// <summary>Versão do driver instalada quando o instalador foi baixado ("" = sem driver).</summary>
    public string InstalledAtDownload { get; set; } = "";
    public DateTime Downloaded { get; set; } = DateTime.Now;
    /// <summary>SHA-256 conferido ao baixar: na hora de executar, o arquivo tem de ser exatamente o mesmo.</summary>
    public string Sha256 { get; set; } = "";
    public bool Launched { get; set; }
}

/// <summary>Estágio da instalação silenciosa. Fraction negativa = sem porcentagem (instalador rodando).</summary>
public sealed record DriverInstallProgress(string Stage, double Fraction);

/// <summary>Resultado de um instalador. Message é o texto para a tela.</summary>
public sealed record SilentInstallResult(bool Success, bool RebootRequired, int? ExitCode, string Message);

/// <summary>
/// Baixa e instala um driver sem janela. Como roda como administrador e sem o usuário ver nada, o arquivo só executa com assinatura válida do
/// fabricante daquele driver: a assinatura de outro fabricante da lista (um cmd.exe da Microsoft, por exemplo) também é recusada.
/// O arquivo fica aberto para leitura da conferência até o fim da execução, então não pode ser trocado no meio.
/// </summary>
public sealed class SilentDriverInstaller
{
    public const string FolderName = "DriverDownloads";
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    private static readonly string[] TrustedPublishers =
    {
        "NVIDIA Corporation", "Advanced Micro Devices", "Advanced Micro Devices, Inc.", "Advanced Micro Devices Inc.", "AMD", "AMD Inc.",
        "Intel Corporation", "Microsoft Corporation", "Realtek Semiconductor", "Realtek Semiconductor Corp.", "Realtek Semiconductor Corp",
    };

    /// <summary>Pasta protegida: só administradores escrevem nela (ver <see cref="SecureFolder"/>).</summary>
    public static string DownloadFolder => SecureFolder.Get(FolderName);

    private readonly string _folder;
    private readonly string _amdRoot;
    private readonly Func<string, string, IProgress<DriverInstallProgress>?, CancellationToken, Task> _download;
    private readonly Func<string, string, string?> _verify;
    private readonly Func<string, string, CancellationToken, Task<int>> _run;

    public SilentDriverInstaller() : this(DownloadFolder, System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "AMD"), DownloadAsync, VerifyForVendor, RunAsync) { }

    /// <param name="verify">Recebe arquivo e fabricante; devolve null se pode executar, ou o motivo da recusa.</param>
    internal SilentDriverInstaller(string folder, string amdRoot, Func<string, string, IProgress<DriverInstallProgress>?, CancellationToken, Task> download,
                                   Func<string, string, string?> verify, Func<string, string, CancellationToken, Task<int>> run)
    {
        _folder = folder; _amdRoot = amdRoot; _download = download; _verify = verify; _run = run;
    }

    internal static string SilentArgs(string vendor) => vendor switch
    {
        "NVIDIA" => "-s -noreboot",
        "AMD" => "-install -boot=false",
        "Intel" => "-s -norestart",
        _ => "/quiet /norestart",
    };

    /// <summary>0 = sucesso; 3010 e 1641 = sucesso, falta reiniciar.</summary>
    internal static bool IsSuccessExitCode(int code) => code is 0 or 3010 or 1641;
    internal static bool RequiresReboot(int code) => code is 3010 or 1641;

    internal static bool IsTrustedPublisher(string subject) => DriverManager.PublisherMatches(subject, TrustedPublishers);

    /// <summary>Quem precisa ter assinado o instalador deste fabricante. Fabricante fora da lista: qualquer um dos confiáveis.</summary>
    internal static bool IsPublisherOf(string subject, string vendor) => vendor switch
    {
        "NVIDIA" => DriverManager.PublisherMatches(subject, "NVIDIA Corporation"),
        "AMD" => DriverManager.PublisherMatches(subject, "Advanced Micro Devices", "Advanced Micro Devices, Inc.", "Advanced Micro Devices Inc.", "AMD", "AMD Inc."),
        "Intel" => DriverManager.PublisherMatches(subject, "Intel Corporation"),
        _ => IsTrustedPublisher(subject),
    };

    /// <summary>WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2) mais o emissor do certificado. Não apaga nada: quem decide é quem chamou.</summary>
    public static bool VerifyAuthenticodeSignature(string filePath, out string summary)
    {
        int trust = CorsairSdkInstaller.WinVerifyTrustFile(filePath);
        if (trust != 0)
        {
            summary = $"Assinatura digital inválida (0x{trust:X8}).";
            return false;
        }
        string subject;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            summary = "Não foi possível ler o certificado: " + ex.Message;
            return false;
        }
        summary = subject;
        return IsTrustedPublisher(subject);
    }

    private static string? VerifyForVendor(string file, string vendor)
    {
        if (!VerifyAuthenticodeSignature(file, out string summary)) return summary;
        return IsPublisherOf(summary, vendor) ? null : $"Assinado por outro fabricante ({summary}).";
    }

    /// <summary>
    /// O instalador que a AMD extraiu em C:\AMD nesta instalação: atisetup.exe, senão Setup.exe, o mais novo. Não entra em atalhos de pasta e
    /// ignora o que já estava lá antes de começar: C:\AMD pode ser criada por qualquer usuário e costuma guardar versões antigas.
    /// </summary>
    internal static string? FindAmdSetup(string root, DateTime notBeforeUtc)
    {
        if (!Directory.Exists(root) || DduCleaner.PassesThroughReparse(root)) return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string name in new[] { "atisetup.exe", "Setup.exe" })
        {
            var hit = new DirectoryInfo(root).EnumerateFiles(name, options).Where(f => f.CreationTimeUtc >= notBeforeUtc).OrderByDescending(f => f.CreationTimeUtc).FirstOrDefault();
            if (hit != null) return hit.FullName;
        }
        return null;
    }

    /// <summary>Instala um driver: baixar, conferir, executar e sempre apagar o instalador temporário. Cancelar lança OperationCanceledException.</summary>
    public async Task<SilentInstallResult> InstallAsync(string vendor, string url, string displayName, IProgress<DriverInstallProgress>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new SilentInstallResult(false, false, null, "Endereço de download recusado (só HTTPS).");
        Directory.CreateDirectory(_folder);
        // Nome novo a cada instalação, nada vindo do servidor: nenhum arquivo antigo é reaproveitado.
        string file = System.IO.Path.Combine(_folder, $"{vendor}-{Guid.NewGuid():N}.exe");
        try
        {
            progress?.Report(new DriverInstallProgress("Baixando", 0));
            await _download(url, file, progress, ct);
            ct.ThrowIfCancellationRequested();
            DateTime startedUtc = DateTime.UtcNow.AddMinutes(-1);
            int code = await RunVerifiedAsync(file, vendor, SilentArgs(vendor), progress, ct);
            if (vendor == "AMD" && !IsSuccessExitCode(code) && FindAmdSetup(_amdRoot, startedUtc) is string extracted)
                code = await RunVerifiedAsync(extracted, vendor, SilentArgs(vendor), progress, ct);
            return Summarize(displayName, code);
        }
        catch (RefusedInstallerException ex)
        {
            Infrastructure.AppLog.Error("driver", ex);
            return new SilentInstallResult(false, false, null, $"{displayName}: {ex.Message} O instalador não foi executado.");
        }
        catch (TimeoutException ex)
        {
            Infrastructure.AppLog.Error("driver", ex);
            return new SilentInstallResult(false, false, null, $"{displayName}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Infrastructure.AppLog.Warn("driver", ex); }
        }
    }

    private static SilentInstallResult Summarize(string name, int code)
    {
        if (!IsSuccessExitCode(code)) return new SilentInstallResult(false, false, code, $"{name}: o instalador terminou com o código {code}.");
        return RequiresReboot(code)
            ? new SilentInstallResult(true, true, code, $"{name}: instalado. Reinicie o PC para concluir.")
            : new SilentInstallResult(true, false, code, $"{name}: instalado.");
    }

    private async Task<int> RunVerifiedAsync(string file, string vendor, string args, IProgress<DriverInstallProgress>? progress, CancellationToken ct)
    {
        string? refused;
        // Compartilha só leitura: enquanto estiver aberto, ninguém troca o arquivo entre a conferência e o fim da execução.
        FileStream hold;
        try { hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Infrastructure.AppLog.Warn("driver", ex);
            throw new RefusedInstallerException("Não foi possível travar o instalador para a conferência (outro programa está usando o arquivo).");
        }
        await using (hold)
        {
            progress?.Report(new DriverInstallProgress("Validando assinatura", -1));
            refused = _verify(file, vendor);
            if (refused == null)
            {
                progress?.Report(new DriverInstallProgress("Instalando", -1));
                return await _run(file, args, ct);
            }
        }
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Infrastructure.AppLog.Warn("driver", ex); }
        throw new RefusedInstallerException(refused);
    }

    private sealed class RefusedInstallerException : Exception
    {
        public RefusedInstallerException(string message) : base(message) { }
    }

    private static Task DownloadAsync(string url, string file, IProgress<DriverInstallProgress>? progress, CancellationToken ct) =>
        DriverManager.DownloadToFileAsync(url, file, "o driver", (_, fraction) => progress?.Report(new DriverInstallProgress("Baixando", fraction)), ct);

    private static async Task<int> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = System.IO.Path.GetDirectoryName(file)!,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("O Windows não iniciou o instalador.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Infrastructure.AppLog.Warn("driver", ex); }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"O instalador passou de {Timeout.TotalMinutes:0} minutos e foi encerrado.");
        }
    }
}

/// <summary>
/// Ciclo do instalador do driver: guarda o que foi baixado, oferece instalar quando a placa está sem driver (depois da
/// DDU) e, quando o driver novo já está instalado, apaga o instalador e as pastas que o instalador extraiu
/// (C:\AMD, C:\NVIDIA), que costumam ocupar de 1 a 3 GB.
/// </summary>
public static class DriverInstallers
{
    public static string OwnFolder => System.IO.Path.Combine(AppSettings.Folder, "drivers");

    public static void Register(AppSettings st, string path, GpuDriverStatus g)
    {
        // Fora da pasta temporária: precisa sobreviver às reinicializações da DDU e à limpeza de temporários do Windows.
        Directory.CreateDirectory(OwnFolder);
        string dest = System.IO.Path.Combine(OwnFolder, System.IO.Path.GetFileName(path));
        if (!string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(path, dest, overwrite: true);
            try { File.Delete(path); } catch { }
        }
        st.PendingDriver = new PendingDriver
        {
            Path = dest, Vendor = g.Vendor, Version = g.Latest, Gpu = g.GpuName, InstalledAtDownload = g.NoDriver ? "" : g.Installed,
            Sha256 = Infrastructure.BinaryTrust.Sha256(dest),
        };
        st.Save();
    }

    public static PendingDriver? Ready(AppSettings st) =>
        st.PendingDriver is { } p && File.Exists(p.Path) ? p : null;

    /// <summary>Costuras de teste: a conferência da assinatura do fabricante e a abertura do instalador.</summary>
    internal static Action<string, string> PublisherCheck = DriverManager.VerifyPublisher;
    internal static Action<string> Starter = path =>
    {
        using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(path)! });
    };

    /// <summary>
    /// Confere e abre o instalador guardado, com o arquivo travado da conferência até o processo nascer: entre conferir e
    /// executar ninguém troca nem altera o arquivo. "" se abriu; senão o motivo (e, se a conferência recusou, o arquivo é apagado).
    /// </summary>
    public static string Launch(AppSettings st)
    {
        if (Ready(st) is not { } p) return "O instalador baixado não está mais no PC. Baixe de novo.";
        var (error, discarded) = LaunchVerified(p);
        if (discarded) st.PendingDriver = null;
        else if (error.Length == 0) p.Launched = true;
        if (discarded || error.Length == 0) st.Save();
        return error;
    }

    /// <summary>A abertura em si, sem tocar nas configurações. Discarded: a conferência recusou e o arquivo foi apagado.</summary>
    internal static (string Error, bool Discarded) LaunchVerified(PendingDriver p)
    {
        try
        {
            Infrastructure.VerifiedLaunch.Run(p.Path, hold => VerifyHeld(p, hold), () => { Starter(p.Path); return true; });
            return ("", false);
        }
        catch (Infrastructure.LaunchRefusedException ex) when (ex.Locked)
        {
            return (ex.Message, false); // nada foi conferido nem apagado: dá para tentar de novo
        }
        catch (Infrastructure.LaunchRefusedException ex)
        {
            Infrastructure.AppLog.Warn("driver", "Instalador guardado recusado: " + ex.Message);
            return (ex.Message + " Baixe de novo.", true);
        }
        catch (Exception ex) { return (ex.Message, false); }
    }

    private static string Publisher(PendingDriver p) => p.Vendor == "AMD" ? "Advanced Micro Devices" : "NVIDIA";

    private static void VerifyHeld(PendingDriver p, FileStream hold)
    {
        // Baixado por uma versão anterior (sem SHA-256 guardado): vale só a assinatura do fabricante.
        if (p.Sha256.Length > 0)
            Infrastructure.VerifiedLaunch.CheckSha256(hold, p.Sha256, "O instalador mudou depois de baixado (SHA-256 diferente).");
        PublisherCheck(p.Path, Publisher(p));
    }

    /// <summary>
    /// Confere o instalador guardado (a pasta e o registro dele ficam no perfil do usuário): o SHA-256 gravado ao baixar e a
    /// assinatura do fabricante. "" se está tudo certo; senão apaga e diz o motivo. Para executar, use <see cref="Launch"/>,
    /// que confere e abre sem intervalo entre as duas coisas.
    /// </summary>
    public static string Verify(AppSettings st)
    {
        if (Ready(st) is not { } p) return "O instalador baixado não está mais no PC. Baixe de novo.";
        try
        {
            if (p.Sha256.Length > 0 && !Infrastructure.BinaryTrust.Sha256(p.Path).Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O instalador mudou depois de baixado (SHA-256 diferente).");
            PublisherCheck(p.Path, Publisher(p));
        }
        catch (Exception ex)
        {
            try { File.Delete(p.Path); }
            catch (Exception del) when (del is IOException or UnauthorizedAccessException) { Infrastructure.AppLog.Warn("driver", del); }
            st.PendingDriver = null; st.Save();
            return ex.Message + " Baixe de novo.";
        }
        return "";
    }

    /// <summary>Pastas que podem ser apagadas: instaladores baixados pelo app e arquivos extraídos pelos instaladores.</summary>
    public static List<string> Leftovers()
    {
        string sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        // Só os caminhos: listar e apagar não dependem de conseguir travar a pasta, e DeleteTree não atravessa atalhos.
        return new[] { OwnFolder, System.IO.Path.Combine(SecureFolder.Root, DriverManager.DownloadFolderName), System.IO.Path.Combine(SecureFolder.Root, SilentDriverInstaller.FolderName), System.IO.Path.Combine(sys, "AMD"), System.IO.Path.Combine(sys, "NVIDIA") }
               .Where(Directory.Exists).ToList();
    }

    public static long Size(IEnumerable<string> dirs) => dirs.Sum(d =>
    {
        try { return new DirectoryInfo(d).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); }
        catch { return 0L; }
    });

    /// <summary>Um instalador de driver está rodando agora (não apagar no meio)?</summary>
    public static bool InstallerRunning() => AnyRunningFrom(Leftovers());

    /// <summary>
    /// Algum processo roda de uma destas pastas? ProcessUtil.ImagePath abre só com QUERY_LIMITED_INFORMATION: processo protegido
    /// devolve null em vez da Win32Exception que o MainModule lançava mais de 100 vezes a cada chamada. Valorant e Vanguard
    /// nem são abertos (regra Vanguard-safe); o nome vem da lista do sistema, sem abrir o processo.
    /// </summary>
    internal static bool AnyRunningFrom(IReadOnlyCollection<string> roots)
    {
        bool found = false;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (found || CompetitiveProfiles.IsVanguardProtected(p.ProcessName)) continue;
                string? path = Infrastructure.ProcessUtil.ImagePath(p);
                found = path != null && roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return found;
    }

    /// <summary>Apaga instaladores e pastas extraídas. Devolve os bytes liberados.</summary>
    public static long Clean(AppSettings st)
    {
        long freed = 0;
        foreach (var dir in Leftovers())
        {
            // C:\AMD e C:\NVIDIA podem ser criadas por qualquer usuário: atalhos de pasta (junctions) não são atravessados.
            freed += DduCleaner.DeleteTree(dir, _ => { }, _ => { }).Bytes;
        }
        st.PendingDriver = null;
        st.Save();
        return freed;
    }

    /// <summary>
    /// Depois da instalação: se o driver da marca do instalador já está instalado (e diferente do que havia ao baixar,
    /// ou o app abriu o instalador) e nenhum instalador está rodando, apaga tudo. Devolve os bytes liberados, ou 0.
    /// </summary>
    public static long CleanupIfInstalled(AppSettings st, List<GpuDriverStatus> gpus)
    {
        if (st.PendingDriver is not { } p) return 0;
        var g = gpus.FirstOrDefault(x => x.Vendor == p.Vendor && !x.NoDriver && x.Installed.Length > 0);
        if (g == null) return 0;
        bool installed = p.Launched || g.Installed != p.InstalledAtDownload;
        if (!installed || InstallerRunning()) return 0;
        return Clean(st);
    }
}
