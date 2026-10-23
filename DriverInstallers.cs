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

    public static string Launch(AppSettings st)
    {
        if (Ready(st) is not { } p) return "O instalador baixado não está mais no PC. Baixe de novo.";
        // A pasta e o registro do instalador ficam no perfil do usuário: confere de novo antes de executar como administrador.
        try
        {
            // Baixado por uma versão anterior (sem SHA-256 guardado): vale só a assinatura do fabricante.
            if (p.Sha256.Length > 0 && !Infrastructure.BinaryTrust.Sha256(p.Path).Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O instalador mudou depois de baixado (SHA-256 diferente).");
            DriverManager.VerifyPublisher(p.Path, p.Vendor == "AMD" ? "Advanced Micro Devices" : "NVIDIA");
        }
        catch (Exception ex)
        {
            try { File.Delete(p.Path); } catch { }
            st.PendingDriver = null; st.Save();
            return ex.Message + " Baixe de novo.";
        }
        try { Process.Start(new ProcessStartInfo(p.Path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(p.Path)! }); }
        catch (Exception ex) { return ex.Message; }
        p.Launched = true;
        st.Save();
        return "";
    }

    /// <summary>Pastas que podem ser apagadas: instaladores baixados pelo app e arquivos extraídos pelos instaladores.</summary>
    public static List<string> Leftovers()
    {
        string sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return new[] { OwnFolder, DriverManager.DownloadFolder, System.IO.Path.Combine(sys, "AMD"), System.IO.Path.Combine(sys, "NVIDIA") }
               .Where(Directory.Exists).ToList();
    }

    public static long Size(IEnumerable<string> dirs) => dirs.Sum(d =>
    {
        try { return new DirectoryInfo(d).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); }
        catch { return 0L; }
    });

    /// <summary>Um instalador de driver está rodando agora (não apagar no meio)?</summary>
    public static bool InstallerRunning()
    {
        var roots = Leftovers();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path != null && roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase))) return true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    /// <summary>Apaga instaladores e pastas extraídas. Devolve os bytes liberados.</summary>
    public static long Clean(AppSettings st)
    {
        long freed = 0;
        foreach (var dir in Leftovers())
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                try { long l = f.Length; f.Attributes = FileAttributes.Normal; f.Delete(); freed += l; } catch { }
            try { Directory.Delete(dir, true); } catch { }
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
