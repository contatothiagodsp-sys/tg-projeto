using System.Diagnostics;
using Microsoft.Win32;

namespace ThgOptimization;

/// <summary>Perfil automático que o Modo Jogo aplica conforme o executável do jogo na tela.</summary>
public enum AutoProfile { Universal, Cs2, Valorant, Lol }

/// <summary>
/// Perfis competitivos do Modo Jogo. CS2: núcleo 0 fora dos núcleos preferidos, prioridade Alta de CPU e de disco nas
/// opções de execução do Windows (IFEO PerfOptions) e sem as otimizações de tela cheia. Valorant: modo Vanguard-safe,
/// o tHg nunca abre o processo do jogo nem os do Vanguard; a prioridade vai só pelo IFEO e o cliente da Riot sai do
/// caminho. League of Legends: durante a partida o cliente vai para os 2 últimos threads com EcoQoS e volta ao normal
/// assim que a partida acaba. Universal: IFEO de alta prioridade, timer mínimo e EcoQoS nos programas de fundo.
/// Tudo que vai para o registro guarda o valor de antes (prefixo "comp.prof.") e volta no "Desfazer tudo" do Competitivo.
/// </summary>
public static class CompetitiveProfiles
{
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
    private const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    public const string ValorantExe = "VALORANT-Win64-Shipping.exe";
    /// <summary>CpuPriorityClass do IFEO: 3 = alta, 6 = acima do normal. IoPriority: 3 = alta.</summary>
    public const int CpuHigh = 3, CpuAboveNormal = 6, IoHigh = 3;

    /// <summary>Processos do Valorant e do Riot Vanguard: o tHg nunca abre (nem para ler o caminho ou a CPU usada).</summary>
    private static readonly HashSet<string> VanguardProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "VALORANT-Win64-Shipping", "VALORANT", "vgc", "vgtray", "vgm",
    };

    public static bool IsVanguardProtected(string processName) => VanguardProtected.Contains(processName);

    public static AutoProfile Detect(string processName) => processName.ToLowerInvariant() switch
    {
        "cs2" => AutoProfile.Cs2,
        "valorant-win64-shipping" => AutoProfile.Valorant,
        "league of legends" => AutoProfile.Lol,
        _ => AutoProfile.Universal,
    };

    public static string Label(AutoProfile p) => p switch
    {
        AutoProfile.Cs2 => "CS2",
        AutoProfile.Valorant => "Valorant",
        AutoProfile.Lol => "League of Legends",
        _ => "Universal / AAA",
    };

    public static string Description(AutoProfile p) => p switch
    {
        AutoProfile.Cs2 => "Núcleo 0 (e o par SMT) fora dos núcleos do jogo, longe das interrupções de mouses de 4/8 kHz; prioridade Alta de CPU e disco pelo Windows; sem otimizações de tela cheia.",
        AutoProfile.Valorant => "Vanguard-safe: o tHg não toca no jogo nem no Vanguard. Prioridade Alta só pelo registro do Windows; cliente da Riot e apps de fundo nos 2 últimos threads com EcoQoS.",
        AutoProfile.Lol => "Durante a partida, LeagueClientUx e os renderizadores do cliente vão para os 2 últimos threads, com EcoQoS e prioridade baixa; voltam ao normal no fim da partida.",
        _ => "Todos os núcleos de desempenho para o jogo, prioridade alta pelo registro do Windows, timer no mínimo suportado e EcoQoS nos apps de fundo.",
    };

    /// <summary>Executáveis que rodam muitos programas (Java, Python, emuladores): PerfOptions neles valeria para todos.</summary>
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "dosbox.exe", "rundll32.exe", "dllhost.exe", "cmd.exe", "conhost.exe",
        "explorer.exe", "wscript.exe", "powershell.exe", "pwsh.exe", "node.exe", "electron.exe", "msedge.exe", "chrome.exe",
    };

    private static bool SafeExe(string exe) => !SharedHosts.Contains(exe) &&
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exe.IndexOfAny(new[] { '\\', '/', ':', '"' }) < 0 && !exe.Contains("..");

    /// <summary>
    /// Grava as PerfOptions do executável (vale a partir da próxima vez que ele abrir). cpu null = não mexe na CPU.
    /// Devolve true se algo mudou. A cópia guarda "valor de antes|valor gravado".
    /// </summary>
    public static bool EnsurePerfOptions(AppSettings s, string exe, int? cpu, int? io)
    {
        if (!SafeExe(exe)) return false;
        bool changed = false;
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey($@"{Ifeo}\{exe}\PerfOptions");
            foreach (var (name, val) in new[] { ("CpuPriorityClass", cpu), ("IoPriority", io) })
            {
                if (val is not int v || k.GetValue(name) is int cur && cur == v) continue;
                string bk = $"comp.prof.ifeo.{name}@{exe}";
                if (!s.TweakBackup.ContainsKey(bk))
                {
                    s.TweakBackup[bk] = $"{(k.GetValue(name) is int prev ? prev.ToString() : "<ausente>")}|{v}";
                    s.Save(); // a cópia vai para o disco antes de mexer no registro
                }
                k.SetValue(name, v, RegistryValueKind.DWord);
                changed = true;
            }
        }
        catch { }
        return changed;
    }

    /// <summary>"Desabilitar otimizações de tela cheia" no .exe (caminho completo). Devolve true se mudou.</summary>
    public static bool EnsureNoFso(AppSettings s, string exePath)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Layers);
            string cur = k.GetValue(exePath) as string ?? "";
            if (cur.Contains(FsoFlag)) return false;
            string bk = $"comp.prof.fso@{exePath}";
            if (!s.TweakBackup.ContainsKey(bk)) { s.TweakBackup[bk] = cur.Length == 0 ? "<ausente>" : cur; s.Save(); }
            GameLauncher.SetDisableFso(exePath, true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Grava agora o que os perfis CS2 e Valorant põem no registro, para valer já na primeira partida.</summary>
    public static List<string> Prepare(AppSettings s)
    {
        var done = new List<string>();
        var cs2 = WindowsTweaks.Cs2Exes();
        if (EnsurePerfOptions(s, "cs2.exe", CpuHigh, IoHigh)) done.Add("CS2: prioridade Alta de CPU e disco");
        if (cs2.Count(e => EnsureNoFso(s, e)) is int n and > 0) done.Add($"CS2: sem otimizações de tela cheia ({n} instalação(ões))");
        if (EnsurePerfOptions(s, ValorantExe, CpuHigh, IoHigh)) done.Add("Valorant: prioridade Alta de CPU e disco");
        return done;
    }

    /// <summary>
    /// Volta o que os perfis gravaram. Só devolve o valor de antes onde o valor atual ainda é o que o tHg gravou: se o
    /// usuário (ou um ajuste da página Competitivo, desfeito antes) mudou depois, fica como está.
    /// </summary>
    public static int RestoreAll(AppSettings s)
    {
        int n = 0;
        foreach (var (bk, backup) in s.TweakBackup.Where(kv => kv.Key.StartsWith("comp.prof.")).ToList())
        {
            try
            {
                if (bk.StartsWith("comp.prof.ifeo.") && bk["comp.prof.ifeo.".Length..].Split('@', 2) is [var name, var exe]
                    && name is "CpuPriorityClass" or "IoPriority" && SafeExe(exe) && backup?.Split('|') is [var prev, var written])
                {
                    string path = $@"{Ifeo}\{exe}\PerfOptions";
                    using (var k = Registry.LocalMachine.OpenSubKey(path, writable: true))
                        if (k != null && k.GetValue(name) is int cur && cur.ToString() == written)
                        {
                            if (int.TryParse(prev, out int p)) k.SetValue(name, p, RegistryValueKind.DWord);
                            else k.DeleteValue(name, false);
                            n++;
                        }
                    DeleteIfEmpty(path);
                    DeleteIfEmpty($@"{Ifeo}\{exe}");
                }
                else if (bk.StartsWith("comp.prof.fso@") && bk["comp.prof.fso@".Length..] is var exePath
                         && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    using var k = Registry.CurrentUser.CreateSubKey(Layers);
                    if ((k.GetValue(exePath) as string ?? "").Contains(FsoFlag))
                    {
                        if (backup is null or "<ausente>") GameLauncher.SetDisableFso(exePath, false);
                        else k.SetValue(exePath, backup, RegistryValueKind.String);
                        n++;
                    }
                }
            }
            catch { }
            s.TweakBackup.Remove(bk);
        }
        s.Save();
        return n;
    }

    /// <summary>Apaga a chave do IFEO que ficou vazia (o tHg a criou só para as PerfOptions).</summary>
    private static void DeleteIfEmpty(string path)
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(path))
                if (k == null || k.ValueCount > 0 || k.SubKeyCount > 0) return;
            Registry.LocalMachine.DeleteSubKey(path, false);
        }
        catch { }
    }

    /// <summary>Perfis com algo gravado no registro agora (para a tela mostrar "pronto").</summary>
    public static bool IsPrepared(string exe)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{Ifeo}\{exe}\PerfOptions");
            return k?.GetValue("CpuPriorityClass") is CpuHigh;
        }
        catch { return false; }
    }
}

/// <summary>
/// Clientes de jogo (LoL e Riot Client) fora do caminho da partida: presos nos 2 últimos threads lógicos, EcoQoS e
/// prioridade baixa; cada um volta à afinidade e à prioridade de antes quando a partida acaba. Os PIDs ficam salvos
/// (ClientDemotedPids): se o tHg fechar no meio, a próxima abertura devolve tudo.
/// </summary>
public sealed class ClientDemoter
{
    public static readonly string[] LolClients = { "LeagueClientUx", "LeagueClientUxRender", "RiotClientUxRender" };
    public static readonly string[] RiotClients = { "RiotClientUx", "RiotClientUxRender" };
    private static readonly HashSet<string> All = new(LolClients.Concat(RiotClients), StringComparer.OrdinalIgnoreCase);

    private readonly AppSettings _s;
    private readonly Dictionary<int, (Process P, IntPtr Affinity, ProcessPriorityClass Prio)> _applied = new();
    private HashSet<string> _targets = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSample;

    public ClientDemoter(AppSettings s) => _s = s;

    public void SetTargets(IEnumerable<string> names) => _targets = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>Rebaixa os clientes ainda não tratados (a cada 10 s no máximo). Retorna quantos entraram agora.</summary>
    public int Sample(bool force = false)
    {
        if (_targets.Count == 0 || !force && (DateTime.Now - _lastSample).TotalSeconds < 10) return 0;
        _lastSample = DateTime.Now;
        ulong? mask = Infrastructure.NativePerfApi.LastTwoCoresMask();
        ulong all = Infrastructure.NativePerfApi.AllCoresMask();
        int n = 0;
        foreach (var name in _targets)
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (_applied.ContainsKey(p.Id)) { p.Dispose(); continue; }
                    var aff = p.ProcessorAffinity;
                    var prio = p.PriorityClass;
                    // Afinidade escolhida pelo usuário ou prioridade fora do normal ficam como estão.
                    if ((ulong)(long)aff != all || prio is not (ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal)) { p.Dispose(); continue; }
                    if (mask is ulong m) p.ProcessorAffinity = (IntPtr)(long)m;
                    // Os renderizadores (Chromium) do cliente só desenham a tela de fundo: ociosos; o LeagueClientUx segue a partida.
                    p.PriorityClass = name.EndsWith("Render", StringComparison.OrdinalIgnoreCase) ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
                    PowerThrottling.SetEco(p, true);
                    _applied[p.Id] = (p, aff, prio);
                    _s.ClientDemotedPids.Add(p.Id);
                    n++;
                }
                catch { try { p.Dispose(); } catch { } }
            }
        if (n > 0) try { _s.Save(); } catch { }
        return n;
    }

    /// <summary>Devolve afinidade, prioridade e EcoQoS de antes. Retorna quantos eram.</summary>
    public int RestoreAll()
    {
        int n = _applied.Count;
        foreach (var (p, aff, prio) in _applied.Values)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.ProcessorAffinity = aff;
                    p.PriorityClass = prio;
                    PowerThrottling.Reset(p);
                }
            }
            catch { }
            p.Dispose();
        }
        _applied.Clear(); _lastSample = default; _targets.Clear();
        Recover(_s);
        return n;
    }

    /// <summary>Depois de um fechamento no meio da partida: quem ainda está preso volta a ter todos os núcleos e prioridade normal.</summary>
    private static void Recover(AppSettings s)
    {
        if (s.ClientDemotedPids.Count == 0) return;
        ulong? mask = Infrastructure.NativePerfApi.LastTwoCoresMask();
        foreach (int pid in s.ClientDemotedPids.ToList())
            try
            {
                using var p = Process.GetProcessById(pid);
                // O PID pode ter sido reaproveitado: só mexe se ainda é um cliente rebaixado.
                if (!All.Contains(p.ProcessName) || p.PriorityClass is not (ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal)) continue;
                if (mask is ulong m && (ulong)(long)p.ProcessorAffinity == m) p.ProcessorAffinity = (IntPtr)(long)Infrastructure.NativePerfApi.AllCoresMask();
                p.PriorityClass = ProcessPriorityClass.Normal;
                PowerThrottling.Reset(p);
            }
            catch { }
        s.ClientDemotedPids.Clear();
        try { s.Save(); } catch { }
    }
}
