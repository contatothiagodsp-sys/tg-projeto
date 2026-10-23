using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace ThgOptimization;

/// <summary>
/// Baixa a versão portátil oficial do OpenRGB (codeberg.org/OpenRGB/OpenRGB, última release) e extrai em
/// %LocalAppData%\Programs\OpenRGB, sem administrador e sem abrir o navegador. O .exe do OpenRGB não tem assinatura
/// digital: a garantia é a origem (só aceita arquivos do Codeberg oficial, por HTTPS).
/// </summary>
public static class OpenRgbInstaller
{
    private const string Api = "https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases/latest";
    // Se a API não responder: release 1.1 (outubro de 2026), a mais nova conhecida quando este código foi escrito.
    private const string FallbackUrl = "https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.1/OpenRGB_1.1_Windows_64_8e16de2.zip";

    public static string TargetDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = Infrastructure.ResilientHttpHandler.CreateClient(TimeSpan.FromMinutes(5));
        c.DefaultRequestHeaders.UserAgent.ParseAdd("tHgOptimization");
        return c;
    }

    private static bool IsCodeberg(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase);

    /// <summary>Baixa e instala. Devolve o caminho do OpenRGB.exe; lança exceção com a explicação se falhar.</summary>
    public static async Task<string> InstallAsync(Action<string> progress, CancellationToken ct = default)
    {
        progress("Consultando a última versão do OpenRGB…");
        string name = Path.GetFileName(FallbackUrl), url = FallbackUrl;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Api, ct));
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string n = a.TryGetProperty("name", out var x) ? x.GetString() ?? "" : "";
                    string u = a.TryGetProperty("browser_download_url", out var y) ? y.GetString() ?? "" : "";
                    if (n.Contains("Windows_64", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && IsCodeberg(u))
                    { name = n; url = u; break; }
                }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { } // sem API: usa a release conhecida

        string tmpDir = Path.Combine(Path.GetTempPath(), "tHgOptimization-orgb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            string zipPath = Path.Combine(tmpDir, "openrgb.zip");
            progress($"Baixando {name}…");
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buf = new byte[81920];
                long done = 0, lastReport = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (done - lastReport > 1_000_000)
                    {
                        lastReport = done;
                        progress(total > 0 ? $"Baixando o OpenRGB… {done / 1_048_576.0:0.0} de {total / 1_048_576.0:0.0} MB" : $"Baixando o OpenRGB… {done / 1_048_576.0:0.0} MB");
                    }
                }
            }

            progress("Instalando o OpenRGB…");
            // Extrai ao lado do destino (mesmo disco), para a troca final ser só um renomear.
            string staging = TargetDir + ".novo";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Extract(zipPath, staging);
            string exe = Path.Combine(staging, "OpenRGB.exe");
            if (!File.Exists(exe)) throw new InvalidOperationException("O pacote baixado não tem o OpenRGB.exe.");
            using (var fs = File.OpenRead(exe))
                if (fs.Length < 1_000_000 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new InvalidOperationException("O OpenRGB.exe baixado não é um programa válido.");

            if (Directory.Exists(TargetDir)) Directory.Delete(TargetDir, true);
            Directory.Move(staging, TargetDir);
            return Path.Combine(TargetDir, "OpenRGB.exe");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
            try { if (Directory.Exists(TargetDir + ".novo")) Directory.Delete(TargetDir + ".novo", true); } catch { }
        }
    }

    /// <summary>Extrai tirando a pasta raiz do pacote ("OpenRGB Windows 64-bit\"), sem deixar nada sair do destino.</summary>
    private static void Extract(string zipPath, string dest)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        string prefix = "";
        var exeEntry = files.FirstOrDefault(e => e.Name.Equals("OpenRGB.exe", StringComparison.OrdinalIgnoreCase));
        if (exeEntry != null) prefix = exeEntry.FullName[..^exeEntry.Name.Length];

        string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        foreach (var e in files)
        {
            if (!e.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string outPath = Path.GetFullPath(Path.Combine(dest, e.FullName[prefix.Length..]));
            if (!outPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            e.ExtractToFile(outPath, true);
        }
    }
}
