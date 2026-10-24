using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThgOptimization;

/// <summary>Programa (agrupado pelo nome do executável) que pode ser fechado para liberar recursos.</summary>
public sealed class BackgroundApp : INotifyPropertyChanged
{
    public string ProcessName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public long MemoryBytes { get; init; }
    public int Count { get; init; }
    public bool HasWindow { get; init; }
    public bool Suggested { get; init; }
    public string MemoryText => $"{MemoryBytes / 1048576.0:N0} MB";

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record CleanupResult(long FreedBytes, int Trimmed, bool StandbyPurged, string? StandbyError);
public sealed record CloseResult(List<string> Closed, List<string> NotClosed);

/// <summary>Limpeza de memória e fechamento de programas em segundo plano.</summary>
public static class Optimizer
{
    /// <summary>Processos que nunca aparecem na lista (sistema, segurança, drivers, o próprio app).</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "StartMenuExperienceHost",
        "ShellExperienceHost", "SearchHost", "SearchIndexer", "TextInputHost", "SecurityHealthService", "SecurityHealthSystray",
        "MsMpEng", "NisSrv", "SgrmBroker", "audiodg", "conhost", "dllhost", "spoolsv", "WmiPrvSE", "LockApp", "ApplicationFrameHost",
        "SystemSettings", "smartscreen", "MemCompression", "Secure System", "WUDFHost", "dasHost", "nvcontainer", "NVDisplay.Container",
        "atiesrxx", "atieclxx", "amdfendrsr", "RadeonSoftware", "igfxEM", "PawnIO", "tHgOptimization", "cmd", "powershell", "WindowsTerminal",
        "OpenConsole", "devenv", "Code", "dotnet",
    };

    /// <summary>Processos de segundo plano seguros de fechar durante o jogo (pré-marcados).</summary>
    private static readonly Dictionary<string, string> SuggestedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OneDrive"] = "Sincronização do OneDrive (pode usar disco e rede)",
        ["ms-teams"] = "Microsoft Teams",
        ["Teams"] = "Microsoft Teams (clássico)",
        ["Skype"] = "Skype",
        ["PhoneExperienceHost"] = "Vincular ao Celular",
        ["YourPhone"] = "Vincular ao Celular",
        ["Widgets"] = "Widgets do Windows",
        ["WidgetService"] = "Serviço de Widgets",
        ["AdobeARM"] = "Atualizador da Adobe",
        ["CCXProcess"] = "Adobe Creative Cloud",
        ["Creative Cloud"] = "Adobe Creative Cloud",
        ["AdobeIPCBroker"] = "Adobe (comunicação em segundo plano)",
        ["GoogleDriveFS"] = "Google Drive",
        ["Dropbox"] = "Dropbox",
        ["Cortana"] = "Cortana",
        ["OfficeClickToRun"] = "Atualizador do Office",
    };

    public static List<BackgroundApp> ListApps()
    {
        int session;
        using (var me = Process.GetCurrentProcess()) session = me.SessionId;
        var groups = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
        // Cada Process segura um handle do processo: todos são liberados no fim (antes ficavam para o coletor de lixo).
        var all = Process.GetProcesses();
        try
        {
            return Group(all, session, groups);
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    private static List<BackgroundApp> Group(Process[] all, int session, Dictionary<string, List<Process>> groups)
    {
        foreach (var p in all)
        {
            try
            {
                if (p.SessionId != session || Protected.Contains(p.ProcessName)) continue;
                if (!groups.TryGetValue(p.ProcessName, out var list)) groups[p.ProcessName] = list = new List<Process>();
                list.Add(p);
            }
            catch { }
        }

        var result = new List<BackgroundApp>();
        foreach (var (name, procs) in groups)
        {
            bool hasWindow = procs.Any(p => SafeWindow(p) != IntPtr.Zero);
            bool suggested = SuggestedApps.ContainsKey(name);
            if (!hasWindow && !suggested) continue; // processos auxiliares sem janela ficam de fora

            string title = name, detail;
            try { title = procs[0].MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : name; } catch { }
            detail = suggested ? SuggestedApps[name] : hasWindow ? "Programa aberto" : "";
            if (procs.Count > 1) detail += $" · {procs.Count} processos";
            long mem = procs.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } });

            result.Add(new BackgroundApp
            {
                ProcessName = name, Title = title, Detail = detail, MemoryBytes = mem, Count = procs.Count,
                HasWindow = hasWindow, Suggested = suggested, Selected = suggested
            });
        }
        return result.OrderByDescending(a => a.Suggested).ThenByDescending(a => a.MemoryBytes).ToList();
    }

    private static IntPtr SafeWindow(Process p) { try { return p.MainWindowHandle; } catch { return IntPtr.Zero; } }

    /// <summary>
    /// Fecha os programas: com janela → pede para fechar (como clicar no X), sem forçar, para não perder nada
    /// não salvo; sem janela (segundo plano) → encerra.
    /// </summary>
    public static CloseResult CloseApps(IEnumerable<BackgroundApp> apps)
    {
        var closed = new List<string>();
        var notClosed = new List<string>();
        foreach (var app in apps)
        {
            var procs = Process.GetProcessesByName(app.ProcessName);
            foreach (var p in procs)
            using (p)
            {
                try
                {
                    if (SafeWindow(p) != IntPtr.Zero) p.CloseMainWindow();
                    else if (app.Suggested) p.Kill();
                }
                catch { }
            }
            // Dá até 4 s para fecharem sozinhos.
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 4 && Infrastructure.ProcessUtil.IsRunning(app.ProcessName)) Thread.Sleep(200);
            if (!Infrastructure.ProcessUtil.IsRunning(app.ProcessName)) closed.Add(app.Title);
            else notClosed.Add(app.Title);
        }
        return new CloseResult(closed, notClosed);
    }

    /// <summary>
    /// Esvazia a lista de espera (standby) da RAM, como o ISLC, e enxuga o working set dos processos.
    /// A lista de espera é cache que o Windows libera sozinho quando precisa; esvaziá-la antes de jogar
    /// ajuda quando a RAM está quase cheia e o jogo engasga ao carregar áreas novas.
    /// </summary>
    /// <param name="excludePid">Processo que não deve ser enxugado (o jogo: tirar a memória dele causa engasgos).</param>
    /// <summary>Processos que não podem perder memória: desenham a tela, tocam o som, protegem o PC ou são anti-cheat.</summary>
    private static readonly HashSet<string> NeverTrim = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "winlogon", "wininit", "lsass", "services", "smss", "audiodg", "explorer", "MsMpEng", "NisSrv",
        "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceitservice",
        "League of Legends", "LeagueClient", "LeagueClientUx", "LeagueClientUxRender", "RiotClientServices",
    };

    public static CleanupResult CleanMemory(int excludePid = -1)
    {
        long before = AvailableBytes();
        int trimmed = 0;
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.Id <= 4 || p.Id == excludePid || NeverTrim.Contains(p.ProcessName)) continue;
                if (EmptyWorkingSet(p.Handle)) trimmed++;
            }
            catch { }
        }

        bool purged = Infrastructure.NativePerfApi.PurgeStandbyList(flushModified: false, out string? error);

        Thread.Sleep(300);
        return new CleanupResult(Math.Max(0, AvailableBytes() - before), trimmed, purged, error);
    }

    public static (long Total, long Available) MemoryStatus()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? ((long)m.ullTotalPhys, (long)m.ullAvailPhys) : (0, 0);
    }

    private static long AvailableBytes() => MemoryStatus().Available;

    /// <summary>Só esvazia a memória em espera (standby), sem mexer na memória dos programas. É o que o ISLC faz durante o jogo.</summary>
    public static bool PurgeStandby() => Infrastructure.NativePerfApi.PurgeStandbyList();

    /// <summary>Ação rápida: grava as páginas modificadas no disco e esvazia a memória em espera (Standby + Modified).</summary>
    public static (bool Ok, long FreedBytes, string? Error) PurgeMemoryLists()
    {
        long before = AvailableBytes();
        bool ok = Infrastructure.NativePerfApi.PurgeStandbyList(flushModified: true, out string? error);
        Thread.Sleep(300);
        return (ok, Math.Max(0, AvailableBytes() - before), error);
    }

    /// <summary>Memória livre de verdade e memória em espera (cache), em bytes. null se o Windows não informar.</summary>
    public static (long Free, long Standby)? MemoryLists()
    {
        var buf = new long[22]; // SYSTEM_MEMORY_LIST_INFORMATION: 5 contadores + 8 standby por prioridade + 8 + 1 (ULONG_PTR)
        int status = NtQuerySystemInformation(80, buf, buf.Length * 8, out _);
        if (status != 0) return null;
        long page = Environment.SystemPageSize;
        long free = (buf[0] + buf[1]) * page;                 // páginas zeradas + livres
        long standby = buf.Skip(5).Take(8).Sum() * page;      // em espera, todas as prioridades
        return (free, standby);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, [Out] long[] info, int length, out int returned);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
