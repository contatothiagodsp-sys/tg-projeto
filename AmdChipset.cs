using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ThgOptimization;

/// <summary>
/// Driver de chipset da AMD (AMD Chipset Software): versão instalada (lista de programas do Windows), a mais nova
/// (página oficial da plataforma: AM5, AM4 ou notebooks) e instalação automática: baixa de drivers.amd.com, confere a
/// assinatura da AMD, instala em silêncio e apaga o instalador.
/// </summary>
public static class AmdChipset
{
    public sealed record Latest(Version Version, string Url, string Page);

    /// <summary>Versão do "AMD Chipset Software" instalado, ou null.</summary>
    public static Version? Installed()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is not string dn || !dn.Contains("AMD Chipset Software", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Parse(k.GetValue("DisplayVersion") as string) is Version v) return v;
                    return new Version(0, 0);
                }
            }
            catch { }
        }
        return null;
    }

    private static Version? Parse(string? s)
    {
        var m = Regex.Match(s ?? "", @"\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>Página da AMD que lista o pacote certo para o processador.</summary>
    public static string PageFor(string cpu)
    {
        const string Base = "https://www.amd.com/en/support/downloads/drivers.html/chipsets/";
        bool mobile = Regex.IsMatch(cpu, @"\d{4}(U|H|HS|HX|HX3D)\b|Ryzen AI|Z1|Z2", RegexOptions.IgnoreCase);
        if (mobile) return Base + "laptop-chipsets/amd-ryzen-and-athlon-mobile-chipset.html";
        var m = Regex.Match(cpu, @"Ryzen\s+\d\s+(?:PRO\s+)?(\d)\d{3}", RegexOptions.IgnoreCase);
        bool am5 = !m.Success || m.Groups[1].Value is "7" or "8" or "9";
        return Base + (am5 ? "am5/x870e.html" : "am4/x570.html");
    }

    public static async Task<Latest?> LatestAsync(string cpu, CancellationToken ct)
    {
        string page = PageFor(cpu);
        string html = await DriverManager.GetAmdPageAsync(page, ct);
        var hits = Regex.Matches(html, @"https://drivers\.amd\.com/drivers/amd_chipset_software_([\d.]+)\.exe", RegexOptions.IgnoreCase)
            .Select(h => (Url: h.Value, V: Parse(h.Groups[1].Value)))
            .Where(h => h.V != null).OrderByDescending(h => h.V).ToList();
        return hits.Count == 0 ? null : new Latest(hits[0].V!, hits[0].Url, page);
    }

    /// <summary>Baixa, confere e instala. Devolve "" quando instalou, ou o motivo.</summary>
    public static string InstallLatest(string cpu, Action<string> status)
    {
        Latest? latest;
        try
        {
            status("Procurando o driver mais novo no site da AMD…");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            latest = LatestAsync(cpu, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) { return "Não foi possível consultar o site da AMD: " + ex.Message; }
        if (latest == null) return "O site da AMD não devolveu o link do driver (pode estar protegido agora). Baixe em amd.com → Suporte → Drivers → Chipsets.";
        if (Installed() is Version cur && cur >= latest.Version) return "";

        string file;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            file = DriverManager.DownloadSignedAsync(latest.Url, "Advanced Micro Devices", $"o chipset AMD {latest.Version}",
                                                     (t, _) => status(t), cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) { return "Falha ao baixar: " + ex.Message; }

        try
        {
            status($"Instalando o chipset AMD {latest.Version}… (a tela pode piscar)");
            // Instalador da AMD aceita /S (silencioso). Se não terminar sozinho, abre a janela normal.
            using var p = Process.Start(new ProcessStartInfo(file, "/S") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(file)! })!;
            if (!p.WaitForExit(15 * 60_000)) return "A instalação está demorando; ela continua em segundo plano. Reinicie o PC quando terminar.";
            Thread.Sleep(3000);
            bool ok = Installed() is Version now && now >= latest.Version;
            if (!ok)
            {
                status("A instalação silenciosa não terminou: abrindo o instalador da AMD…");
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.WaitForExit(30 * 60_000);
                ok = Installed() is Version again && again >= latest.Version;
            }
            if (!ok) return $"O instalador da AMD terminou sem instalar (código {p.ExitCode}). Tente pelo site da AMD.";
            return "";
        }
        catch (Exception ex) { return "Falha ao instalar: " + ex.Message; }
        finally
        {
            try { File.Delete(file); } catch { } // ~80 MB: não fica ocupando espaço
            try { Directory.Delete(@"C:\AMD\Chipset_Software", true); } catch { }
        }
    }
}
