using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ThgOptimization;

/// <summary>Pasta ou cache que pode ser esvaziado com segurança (o Windows e os programas recriam o que precisarem).</summary>
public sealed class CleanTarget : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    internal Func<IEnumerable<string>> Folders { get; init; } = () => Array.Empty<string>();
    /// <summary>Só apaga arquivos sem alteração há este tempo (instaladores e programas abertos usam a pasta temporária).</summary>
    internal TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    /// <summary>Limpeza feita por outro caminho (lixeira, cmdlet do Windows) em vez de apagar arquivo por arquivo.</summary>
    internal Func<long>? Custom { get; init; }

    private bool _selected = true;
    public bool Selected { get => _selected; set { _selected = value; Changed(nameof(Selected)); } }
    private long _bytes = -1;
    public long Bytes { get => _bytes; set { _bytes = value; Changed(nameof(Bytes)); Changed(nameof(SizeText)); } }
    public string SizeText => _bytes < 0 ? "…" : WindowsCleanup.Size(_bytes);

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>App que vem com o Windows (ou instalado como propaganda) e pode ser removido.</summary>
public sealed class BloatApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Package { get; init; } = "";
    public string Hint { get; init; } = "";
    private bool _selected;
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Limpeza do Windows: arquivos temporários e caches (seguros de apagar) e apps pré-instalados. Os apps são removidos só
/// da conta atual, sem apagar a cópia que o Windows guarda, por isso "Reinstalar removidos" consegue trazê-los de volta.
/// Apps do Xbox, Loja, Calculadora, Fotos e afins nunca entram na lista.
/// </summary>
public static class WindowsCleanup
{
    public static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : b > 0 ? $"{Math.Max(1, b / 1024)} KB" : "0 MB";

    public static List<CleanTarget> Targets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new List<CleanTarget>
        {
            new() { Id = "usertemp", Title = "Arquivos temporários do usuário", Detail = "Pasta %TEMP%: sobras de instaladores, atualizações e programas. Arquivos da última hora ficam.",
                    Folders = () => new[] { Path.GetTempPath() }, MinAge = TimeSpan.FromHours(1) },
            new() { Id = "wintemp", Title = "Arquivos temporários do Windows", Detail = @"Pasta C:\Windows\Temp. Arquivos da última hora ficam.",
                    Folders = () => new[] { Path.Combine(win, "Temp") }, MinAge = TimeSpan.FromHours(1) },
            new() { Id = "wer", Title = "Relatórios de erro do Windows", Detail = "Relatórios de travamentos já enviados ou na fila. Os minidumps de tela azul ficam (servem para diagnóstico).",
                    Folders = () => new[] { Path.Combine(pd, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(pd, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(local, @"Microsoft\Windows\WER"), Path.Combine(local, "CrashDumps") } },
            new() { Id = "dowu", Title = "Cache de entrega de atualizações", Detail = "Cópias de atualizações que o Windows guarda para compartilhar (Otimização de Entrega). Já foram instaladas.",
                    Folders = () => new[] { Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), Path.Combine(win, @"SoftwareDistribution\DeliveryOptimization") },
                    Custom = () => { SecurityAudit.Ps("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue", 120_000); return 0; } },
            new() { Id = "shaders", Title = "Cache de shaders da placa de vídeo", Detail = "Recriado pelos jogos. Vale limpar depois de trocar o driver; os primeiros minutos de cada jogo podem engasgar enquanto ele se refaz.",
                    Folders = ShaderCache.Folders, Selected = false },
            new() { Id = "recycle", Title = "Lixeira", Detail = "Esvazia a lixeira de todos os discos. Não dá para recuperar depois.", Selected = false,
                    Custom = () => { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); return 0; } },
        };
    }

    private static readonly System.IO.EnumerationOptions Deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    public static long Measure(CleanTarget t)
    {
        if (t.Id == "recycle") return RecycleBinSize();
        long sum = 0;
        DateTime cut = DateTime.Now - t.MinAge;
        foreach (var d in t.Folders().Where(Directory.Exists))
            try { foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", Deep)) if (f.LastWriteTime <= cut) sum += f.Length; }
            catch { }
        return sum;
    }

    /// <summary>Apaga o que der (arquivos em uso ficam) e devolve os bytes liberados.</summary>
    public static long Clean(CleanTarget t, Action<string> log)
    {
        long before = Measure(t), freed = 0;
        int skipped = 0;
        DateTime cut = DateTime.Now - t.MinAge;
        foreach (var d in t.Folders().Where(Directory.Exists))
        {
            try
            {
                foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", Deep))
                {
                    if (f.LastWriteTime > cut) continue;
                    try { long l = f.Length; f.Attributes = FileAttributes.Normal; f.Delete(); freed += l; }
                    catch { skipped++; }
                }
                // Pastas que ficaram vazias (da mais funda para a raiz; a raiz fica).
                foreach (var sub in new DirectoryInfo(d).EnumerateDirectories("*", Deep).OrderByDescending(x => x.FullName.Length))
                    try { if (!sub.EnumerateFileSystemInfos().Any()) sub.Delete(); } catch { }
            }
            catch { }
        }
        if (t.Custom != null)
        {
            try { t.Custom(); } catch { }
            freed = Math.Max(freed, before - Measure(t));
        }
        log($"{t.Title}: {Size(freed)} liberados" + (skipped > 0 ? $" ({skipped} arquivo(s) em uso ficaram)" : "") + ".");
        return freed;
    }

    private static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    // ------------------------------------------------------------------ apps pré-instalados

    /// <summary>Catálogo: nome do pacote (ou prefixo terminado em *), nome amigável, marcado por padrão e observação.</summary>
    private static readonly (string Package, string Name, bool Default, string Hint)[] Catalog =
    {
        ("Microsoft.BingNews", "Notícias", true, ""),
        ("Microsoft.BingWeather", "Clima", false, "Tem quem use: desmarcado por padrão."),
        ("Microsoft.BingSearch", "Pesquisa do Bing", true, ""),
        ("Microsoft.GetHelp", "Obter Ajuda", true, ""),
        ("Microsoft.Getstarted", "Dicas", true, ""),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", true, "Tem anúncios."),
        ("Microsoft.People", "Pessoas", true, ""),
        ("Microsoft.WindowsFeedbackHub", "Hub de Comentários", true, ""),
        ("Microsoft.WindowsMaps", "Mapas", true, ""),
        ("Microsoft.ZuneVideo", "Filmes e TV", true, ""),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (atalho do Office)", true, "Só um atalho para assinar o Office; o Office instalado não é afetado."),
        ("Microsoft.549981C3F5F10", "Cortana", true, ""),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", true, ""),
        ("Microsoft.MixedReality.Portal", "Portal de Realidade Misturada", true, ""),
        ("Microsoft.SkypeApp", "Skype", true, ""),
        ("Microsoft.Microsoft3DViewer", "Visualizador 3D", true, ""),
        ("Microsoft.Office.OneNote", "OneNote (versão da Loja)", false, ""),
        ("Microsoft.Todos", "Microsoft To Do", false, ""),
        ("Microsoft.YourPhone", "Link to Phone (Celular)", false, "Liga o celular ao PC: desmarcado por padrão."),
        ("Microsoft.WindowsAlarms", "Alarmes e Relógio", false, ""),
        ("Microsoft.WindowsSoundRecorder", "Gravador de Som", false, ""),
        ("Microsoft.OutlookForWindows", "Outlook (novo)", false, ""),
        ("MicrosoftCorporationII.QuickAssist", "Assistência Rápida", false, "Usado para suporte remoto."),
        ("MicrosoftTeams", "Teams (pessoal)", true, ""),
        ("MSTeams", "Teams", false, "Desmarcado: pode ser o Teams do trabalho."),
        ("Clipchamp.Clipchamp", "Clipchamp", true, ""),
        ("Microsoft.Copilot", "Copilot (app)", true, ""),
        ("Microsoft.Windows.DevHome", "Dev Home", true, ""),
        ("SpotifyAB.SpotifyMusic", "Spotify (instalado pela propaganda)", false, "Desmarcado: muita gente usa."),
        ("Disney.*", "Disney+", true, ""),
        ("AmazonVideo.PrimeVideo", "Prime Video", true, ""),
        ("BytedancePte.Ltd.TikTok", "TikTok", true, ""),
        ("Facebook.*", "Facebook / Instagram", true, ""),
        ("king.com.*", "Candy Crush e outros jogos da King", true, "Instalados pela propaganda do Windows."),
        ("*.LinkedInforWindows", "LinkedIn", true, ""),
        ("5A894077.McAfeeSecurity", "McAfee (versão de teste)", false, ""),
    };

    /// <summary>Nunca removidos, mesmo que alguém peça (Xbox e Game Bar são usados pelos jogos).</summary>
    private static readonly string[] Keep =
    {
        "Microsoft.GamingApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI", "Microsoft.XboxSpeechToTextOverlay",
        "Microsoft.GamingServices", "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.WindowsCalculator",
        "Microsoft.Windows.Photos", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.WindowsTerminal", "Microsoft.VCLibs",
        "Microsoft.UI.Xaml", "Microsoft.NET", "Microsoft.HEIFImageExtension", "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions", "Microsoft.SecHealthUI",
    };

    private static bool Matches(string pattern, string name) =>
        pattern.StartsWith('*') ? name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
        : pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>Apps do catálogo que estão instalados nesta conta.</summary>
    public static List<BloatApp> InstalledBloat()
    {
        var names = new List<string>();
        try
        {
            string json = SecurityAudit.Ps("Get-AppxPackage | Select-Object -ExpandProperty Name | ConvertTo-Json -Compress", 60_000, returnOutput: true).Trim();
            if (json.StartsWith('[')) names = JsonSerializer.Deserialize<List<string>>(json) ?? new();
            else if (json.StartsWith('"')) names.Add(JsonSerializer.Deserialize<string>(json) ?? "");
        }
        catch { }
        var list = new List<BloatApp>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Keep.Any(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;
            var c = Catalog.FirstOrDefault(c => Matches(c.Package, name));
            if (c.Package == null) continue;
            if (list.Any(b => b.Name == c.Name)) continue;
            list.Add(new BloatApp { Name = c.Name, Package = name, Hint = c.Hint, Selected = c.Default });
        }
        return list.OrderBy(b => b.Selected ? 0 : 1).ThenBy(b => b.Name).ToList();
    }

    /// <summary>Remove da conta atual. Devolve os pacotes removidos (guardados para "Reinstalar").</summary>
    public static List<string> Remove(IEnumerable<BloatApp> apps, Action<string> log)
    {
        var done = new List<string>();
        foreach (var a in apps)
        {
            if (Keep.Any(k => a.Package.StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;
            string pkg = a.Package.Replace("'", "''");
            string r = SecurityAudit.Ps($"Get-AppxPackage -Name '{pkg}' | Remove-AppxPackage -ErrorAction Stop; 'ok'", 120_000, returnOutput: true);
            bool ok = r.Contains("ok");
            log(ok ? $"Removido: {a.Name}" : $"Não removido: {a.Name}. {r.Trim()}");
            if (ok) done.Add(a.Package);
        }
        return done;
    }

    /// <summary>Registra de novo a cópia que o Windows guarda. Sem cópia, o app volta pela Microsoft Store.</summary>
    public static List<string> Reinstall(IEnumerable<string> packages, Action<string> log)
    {
        var back = new List<string>();
        foreach (var p in packages)
        {
            string pkg = p.Replace("'", "''");
            string r = SecurityAudit.Ps(
                $"$p = Get-AppxPackage -AllUsers -Name '{pkg}' | Where-Object InstallLocation | Select-Object -First 1; " +
                "if (-not $p) { 'none' } else { Add-AppxPackage -Register (Join-Path $p.InstallLocation 'AppXManifest.xml') -DisableDevelopmentMode -ErrorAction Stop; 'ok' }",
                180_000, returnOutput: true);
            bool ok = r.Contains("ok");
            log(ok ? $"Reinstalado: {p}" : $"{p}: sem cópia no PC. Instale de novo pela Microsoft Store.");
            if (ok) back.Add(p);
        }
        return back;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
}
