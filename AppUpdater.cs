using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThgOptimization;

public sealed record UpdateInfo(Version Version, string Url, string? Sha256, long Size, string Notes);

/// <summary>
/// Atualização pelo GitHub Releases: a release "latest" (gerada pelo GitHub Actions a cada envio) traz o instalador e a
/// versão no título. O instalador é baixado, conferido pelo SHA-256 que o próprio GitHub publica e executado em modo
/// silencioso: ele fecha o app, troca os arquivos e abre a versão nova. As configurações ficam.
/// O repositório vem do build (-p:UpdateRepo=dono/repo); builds locais não têm, e a busca fica desligada.
/// </summary>
public static class AppUpdater
{
    public static string? Repo =>
        typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { Length: > 2 } r ? r : null;

    public static Version Current
    {
        get
        {
            var v = typeof(AppUpdater).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>
    /// O que o usuário vê: só Major.Minor. O terceiro número é o contador do CI e continua existindo por dentro, porque é ele
    /// que faz cada envio ser maior que o anterior. Diagnósticos, relatórios de falha e dicas mostram a versão completa.
    /// </summary>
    public static string Display(Version v) => $"{v.Major}.{v.Minor}";

    public static string CurrentDisplay => Display(Current);

    private static bool SameDisplay(Version current, Version target) => Display(current) == Display(target);

    internal static string ActionLabel(Version current, Version target) =>
        SameDisplay(current, target) ? "Instalar atualização" : $"Atualizar para a versão {Display(target)}";

    internal static string AvailableLabel(Version current, Version target) =>
        SameDisplay(current, target) ? "Atualização disponível" : $"Atualização {Display(target)} disponível";

    internal static string AvailableMessage(Version current, Version target) =>
        SameDisplay(current, target) ? $"Há uma atualização da versão {Display(target)}." : $"A versão {Display(target)} está disponível.";

    private static HttpClient Client()
    {
        var c = Infrastructure.ResilientHttpHandler.CreateClient(TimeSpan.FromSeconds(30));
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"tHgOptimization/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Versão nova disponível, ou null (sem internet, sem repositório, ou já está na mais nova).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (Repo is not string repo) return null;
        using var http = Client();
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct));
        var root = doc.RootElement;
        string title = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (ParseVersion(tag, title) is not Version latest || latest <= Current) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            if (!Regex.IsMatch(name, @"^tHgOptimization-Setup.*\.exe$", RegexOptions.IgnoreCase)) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is string dg && dg.StartsWith("sha256:") ? dg[7..] : null;
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            // Só HTTPS do próprio GitHub, e só com o SHA-256 publicado pelo GitHub para conferir o arquivo.
            if (sha == null || !IsGitHub(url)) return null;
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new UpdateInfo(latest, url, sha, a.GetProperty("size").GetInt64(), notes);
        }
        return null;
    }

    /// <summary>Versão da release: a tag manda (v1.2.3); o título só conta quando ela não traz versão (a tag "latest").</summary>
    internal static Version? ParseVersion(string tag, string title)
    {
        const string Pattern = @"\d+\.\d+\.\d+";
        var m = Regex.Match(tag, Pattern);
        if (!m.Success) m = Regex.Match(title, Pattern);
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    public static bool IsGitHub(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host == "github.com" || u.Host == "api.github.com" || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Baixa o instalador (com progresso de 0 a 1) e confere o SHA-256. Devolve o caminho do arquivo.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct)
    {
        string dir = SecureFolder.Get("update"); // protegida: o instalador não pode ser trocado depois de conferido
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir)) try { File.Delete(old); } catch { }
        string file = Path.Combine(dir, $"tHgOptimization-Setup-{u.Version}.exe");

        using var http = Client();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var resp = await http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? u.Size;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file))
        {
            var buf = new byte[81920];
            long done = 0; int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        if (u.Sha256 == null) throw new InvalidDataException("A versão nova não tem SHA-256 publicado: atualização recusada por segurança.");
        try { await Task.Run(() => Infrastructure.BinaryTrust.VerifyUpdate(file, u.Sha256), ct); }
        catch { try { File.Delete(file); } catch { } throw; }
        return file;
    }

    /// <summary>Executa o instalador em silêncio (conferido de novo antes). Ele fecha o tHg, atualiza e abre a versão nova sozinho.</summary>
    public static void RunInstaller(string file, UpdateInfo u)
    {
        Infrastructure.BinaryTrust.VerifyUpdate(file, u.Sha256 ?? throw new InvalidDataException("Sem SHA-256 publicado."));
        Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /UPDATE")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(file)!,
        });
    }
}
