using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ThgOptimization;

/// <summary>Uma sessão de jogo medida pelo Modo Jogo (para o "antes e depois" do TURBO).</summary>
public sealed record GameSessionRecord(DateTime Start, string Game, double Minutes, double AvgFps, double Low1Fps, bool AfterTurbo,
                                       double Low01Fps = 0, int Stutters = 0);

/// <summary>
/// Modo Jogo automático (no estilo Process Lasso / Razer Cortex, sem ficar residente no jogo): percebe quando um jogo
/// da biblioteca vai para a tela e, enquanto ele roda, dá prioridade ao processo, limpa a memória em espera, ativa o plano
/// de alto desempenho e, se o usuário quiser, prende o jogo nos núcleos mais rápidos para jogos (CCD com 3D V-Cache
/// nos Ryzen X3D de dois CCDs, núcleos P nos Intel híbridos). Quando o jogo fecha, desfaz tudo e grava FPS médio e 1% low.
/// </summary>
public sealed class GameModeEngine : IDisposable
{
    public event Action<string, string>? Notify;

    private readonly AppSettings _s;
    private List<LibGame> _library = new();
    private HashSet<string> _libraryExes = new(StringComparer.OrdinalIgnoreCase);

    private Process? _game;
    private string _gameName = "";
    private DateTime _start;
    private string? _prevScheme;
    private bool _timerSet;
    private readonly BackgroundBalancer _balancer = new();
    private readonly BackgroundEcoQos _eco = new();
    private DateTime _lastPurge;
    private int _purges;
    private double _fpsSum, _lowSum;
    private int _samples;

    public string Status { get; private set; } = "Desligado";
    public bool Active => _game != null;
    public string CpuName { get; set; } = "";

    public GameModeEngine(AppSettings s) => _s = s;

    public void SetLibrary(List<LibGame> library)
    {
        _library = library;
        _libraryExes = new HashSet<string>(library.Where(g => g.ExePath != null).Select(g => Path.GetFileName(g.ExePath!)), StringComparer.OrdinalIgnoreCase);
        foreach (var p in NvDriverProfiles.Catalog) foreach (var e in p.Exes) _libraryExes.Add(e);
    }

    /// <summary>Chamado a cada poucos segundos pela janela.</summary>
    private FrameMonitor? _fm;
    private int _captureId;

    public void Tick(FrameMonitor frames)
    {
        _fm = frames;
        if (_game != null)
        {
            bool exited;
            try { exited = _game.HasExited; } catch { exited = true; }
            if (exited) { End(); return; }
            if (frames.GetStats(_game.Id) is { Fps: > 1 } f) { _fpsSum += f.Fps; _lowSum += f.Low1Fps; _samples++; }
            WatchStandby();
            if (_s.GameModeProBalance) _balancer.Sample(_game.Id, IsGameFamily);
            if (_s.GameModeEcoQos) _eco.Sample(_game.Id, IsGameFamily); // navegadores e launchers abertos depois do jogo também
            return;
        }
        if (!_s.AutoGameMode) { Status = "Desligado"; return; }
        Status = "Aguardando um jogo abrir…";

        var p = ForegroundProcess();
        if (p == null || !IsGame(p, out string name)) return;
        Begin(p, name);
    }

    /// <summary>
    /// Clientes, launchers e processos auxiliares que ficam na mesma pasta do jogo (o cliente do LoL, por exemplo, fica em
    /// "Riot Games\League of Legends" junto com o jogo). Nunca são tratados como o jogo: o Modo Jogo espera o jogo de verdade.
    /// </summary>
    private static readonly Regex NotAGame = new(
        @"^(LeagueClient|LeagueClientUx|LeagueClientUxRender|LeagueCrashHandler\w*|RiotClient\w*|Riot Client|steam|steamwebhelper|" +
        @"EpicGamesLauncher|EpicWebHelper|Battle\.net|Agent|EADesktop|EABackgroundService|EALauncher|upc|UbisoftConnect|GalaxyClient\w*|" +
        @"Overwolf\w*|\w*Launcher\w*|\w*CrashReport\w*|\w*CrashHandler\w*|UnrealCEFSubProcess|CefSharp\.BrowserSubprocess|QtWebEngineProcess|" +
        @"\w*Updater|\w*Setup|unins\d*|vc_redist\w*|DXSETUP|\w*Installer\w*)$", RegexOptions.IgnoreCase);

    /// <summary>Processo do mesmo jogo (mesma pasta de instalação ou exe conhecido): o ProBalance nunca rebaixa.</summary>
    internal bool IsGameFamily(Process p)
    {
        if (_libraryExes.Contains(p.ProcessName + ".exe")) return true;
        if (_gameDir == null) return false;
        try { return p.MainModule?.FileName?.StartsWith(_gameDir, StringComparison.OrdinalIgnoreCase) == true; } catch { return false; }
    }

    private string? _gameDir;

    private bool IsGame(Process p, out string name)
    {
        name = "";
        if (NotAGame.IsMatch(p.ProcessName)) return false;
        string? path;
        try { path = p.MainModule?.FileName; } catch { return false; } // processos protegidos (anti-cheat) podem negar: usa o nome
        string exe = path != null ? Path.GetFileName(path) : p.ProcessName + ".exe";
        if (NvDriverProfiles.ByExe(exe) is { } preset) { name = preset.Name; return true; }
        if (path == null) return false;
        var lib = _library.FirstOrDefault(g => g.InstallDir.Length > 3 && path.StartsWith(g.InstallDir, StringComparison.OrdinalIgnoreCase))
                  ?? _library.FirstOrDefault(g => g.ExePath != null && path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase));
        if (lib == null) return false;
        name = lib.Name;
        return true;
    }

    private void Begin(Process p, string name)
    {
        _game = p; _gameName = name; _start = DateTime.Now; _fpsSum = _lowSum = 0; _samples = 0;
        try { _gameDir = Path.GetDirectoryName(Path.GetDirectoryName(p.MainModule?.FileName ?? "")); } catch { _gameDir = null; }
        if (_gameDir is { Length: <= 3 }) _gameDir = null; // nunca a raiz de um disco
        var done = new List<string>();

        // Prioridade: a do perfil do jogo (Biblioteca), ou "acima do normal". "Alta" só se o usuário escolheu no perfil.
        var profile = ProfileFor(p);
        var prio = profile?.Priority switch { "high" => ProcessPriorityClass.High, "normal" => ProcessPriorityClass.Normal, _ => ProcessPriorityClass.AboveNormal };
        try { p.PriorityClass = prio; done.Add(prio == ProcessPriorityClass.High ? "prioridade alta" : prio == ProcessPriorityClass.Normal ? "prioridade normal" : "prioridade acima do normal"); } catch { }

        // Núcleos preferidos por CPU Sets (o Windows ainda pode usar os outros se precisar: mais suave que a afinidade e
        // não incomoda anti-cheats); se o Windows não aceitar, cai para a afinidade.
        if (_s.GameModeAffinity && CpuTopology.GameCoresMask(out string what) is ulong mask)
        {
            if (CpuTopology.SetPreferredCores(p, mask)) done.Add(what);
            else try { p.ProcessorAffinity = (IntPtr)(long)mask; done.Add(what); } catch { }
        }

        _captureId = _fm?.StartCapture(p.Id) ?? 0;

        if (_s.GameModePowerPlan && !WindowsTweaks.IsDualCcdX3D(CpuName))
            try
            {
                string? cur = PowerPlan.Active();
                if (cur != null && !PowerPlan.IsGamingPlan(cur) && PowerPlan.ActivateBest() == "")
                {
                    _prevScheme = cur; done.Add("alto desempenho");
                }
            }
            catch { }

        if (_s.GameModeCloseApps)
            try
            {
                var r = Optimizer.CloseApps(Optimizer.ListApps().Where(a => a.Suggested));
                if (r.Closed.Count > 0) done.Add($"fechados: {string.Join(", ", r.Closed)}");
            }
            catch { }

        // Na hora em que o jogo abre não se mexe na memória de ninguém: enxugar processos (inclusive o DWM, que desenha a tela)
        // e esvaziar o cache que o jogo acabou de ler causava engasgos e até o jogo minimizar. Só com a RAM quase cheia
        // (menos de 15% livre) esvazia a memória em espera.
        if (_s.GameModeCleanRam)
            try
            {
                var (total, avail) = Optimizer.MemoryStatus();
                if (total > 0 && avail < total * 0.15 && Optimizer.PurgeStandby()) done.Add("memória em espera liberada (RAM quase cheia)");
            }
            catch { }

        // Timer do Windows em 0,5 ms enquanto o jogo roda (como o TimerResolution): frametime mais regular em muitos jogos.
        // No Windows 11 só vale para o jogo com o ajuste "Resolução de timer… vale sempre" do OTIMIZA ligado.
        if (_s.GameModeTimer)
            try { if (NtSetTimerResolution(5000, true, out _) == 0) { _timerSet = true; done.Add("timer em 0,5 ms"); } } catch { }

        if (_s.GameModeQuiet && Notifications.Mute(_s)) done.Add("notificações silenciadas");

        // Monitor na taxa máxima (alguns jogos e trocas de resolução deixam em 60 Hz).
        if (_s.GameModeMaxRefresh)
            try
            {
                foreach (var d in DisplayTuner.List().Where(d => d.BelowMax))
                    if (DisplayTuner.SetMaxRefresh(d, _s) is { Length: 0 }) done.Add($"monitor em {d.MaxHz} Hz");
            }
            catch { }

        // Prioridade de disco do jogo em "alta" (o Windows pode recusar sem privilégio especial; aí fica a normal).
        if (ProcessIo.SetIoPriority(p, 3)) done.Add("disco com prioridade alta");

        // O jogo fica explicitamente fora do EcoQoS (o Windows 11 nunca o joga nos núcleos econômicos nem baixa o clock dele);
        // navegadores, launchers e Discord abertos em segundo plano entram no modo de eficiência até o jogo fechar.
        PowerThrottling.SetEco(p, false);
        if (_s.GameModeEcoQos && _eco.Sample(p.Id, IsGameFamily, force: true) is int eco and > 0) done.Add($"{eco} processo(s) de fundo em modo de eficiência");

        // SysMain e Windows Search ficam parados durante o jogo (leem e indexam o disco em segundo plano).
        if (_s.GameModeServices && GameServices.Stop(_s) is int stopped and > 0) done.Add($"{stopped} serviço(s) de disco pausado(s)");

        // Programas pesados escolhidos pelo usuário ficam pausados (não fechados) até o jogo fechar.
        if (_s.GameModeSuspend && ProcessPause.Pause(_s, p.Id) is int paused and > 0) done.Add($"{paused} programa(s) pausado(s)");

        Status = $"ATIVO · {name}";
        Notify?.Invoke($"Modo Jogo · {name}", done.Count > 0 ? string.Join(" · ", done) : "Monitorando a sessão");
    }

    /// <summary>
    /// Como o ISLC, mas só em aperto de verdade: o Windows reaproveita a memória em espera sem custo, e esvaziá-la à toa
    /// faz o jogo e o sistema lerem de novo do disco (engasgo). Só esvazia se a memória livre cair abaixo de 256 MB com
    /// mais de 1 GB em espera, no máximo a cada 60 s.
    /// </summary>
    private void WatchStandby()
    {
        if (!_s.GameModeCleanRam || (DateTime.Now - _lastPurge).TotalSeconds < 60) return;
        if (Optimizer.MemoryLists() is not { } m) return;
        if (m.Free < 256L << 20 && m.Standby > 1L << 30)
        {
            _lastPurge = DateTime.Now;
            if (Optimizer.PurgeStandby()) _purges++;
        }
    }

    private GameProfile? ProfileFor(Process p)
    {
        string? path = null;
        try { path = p.MainModule?.FileName; } catch { }
        if (path == null) return null;
        var lib = _library.FirstOrDefault(g => g.ExePath != null && g.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                  ?? _library.FirstOrDefault(g => g.InstallDir.Length > 3 && path.StartsWith(g.InstallDir, StringComparison.OrdinalIgnoreCase));
        return lib != null && _s.GameProfiles.TryGetValue(lib.Key, out var prof) ? prof : null;
    }

    private void End()
    {
        if (_timerSet) { try { NtSetTimerResolution(5000, false, out _); } catch { } _timerSet = false; }
        int restrained = _balancer.RestoreAll();
        _eco.RestoreAll();
        if (_game != null) try { if (!_game.HasExited) PowerThrottling.Reset(_game); } catch { }
        Notifications.Restore(_s);
        ProcessPause.ResumeAll(_s);
        GameServices.Restart(_s);
        var times = _captureId != 0 && _fm != null ? _fm.StopCapture(_captureId) : new List<double>();
        _captureId = 0;
        double minutes = (DateTime.Now - _start).TotalMinutes;
        if (_prevScheme != null) { try { PowerPlan.Activate(_prevScheme); } catch { } _prevScheme = null; }
        string text = $"{minutes:0} min de jogo";
        if (_samples >= 10 && minutes >= 1)
        {
            // Com a gravação completa: médias exatas (todos os frames), 0,1% low e engasgos. Sem ela, a média das amostras.
            var a = times.Count > 600 ? FrameAnalysis.Analyze(_gameName, _start, times) : null;
            var rec = new GameSessionRecord(_start, _gameName, Math.Round(minutes, 1),
                                            Math.Round(a?.AvgFps ?? _fpsSum / _samples, 1), Math.Round(a?.Low1Fps ?? _lowSum / _samples, 1),
                                            _s.TurboAppliedAt is DateTime t && _start >= t,
                                            Math.Round(a?.Low01Fps ?? 0, 1), a?.Stutters ?? 0);
            _s.GameSessions.Insert(0, rec);
            if (_s.GameSessions.Count > 300) _s.GameSessions.RemoveRange(300, _s.GameSessions.Count - 300);
            _s.Save();
            text += $" · {rec.AvgFps:0} FPS médio · 1% low {rec.Low1Fps:0}" + (rec.Low01Fps > 0 ? $" · 0,1% {rec.Low01Fps:0} · {rec.Stutters} engasgo(s)" : "");
        }
        if (_purges > 0) text += $" · memória em espera esvaziada {_purges}x";
        if (restrained > 0) text += $" · {restrained} programa(s) de fundo seguraram a CPU e foram rebaixados";
        _purges = 0;
        Notify?.Invoke($"Modo Jogo encerrado · {_gameName}", text + " · Windows voltou ao normal");
        _game = null;
        Status = _s.AutoGameMode ? "Aguardando um jogo abrir…" : "Desligado";
    }

    public void Dispose()
    {
        _balancer.RestoreAll();
        _eco.RestoreAll();
        Notifications.Restore(_s);
        ProcessPause.ResumeAll(_s);
        GameServices.Restart(_s);
        if (_prevScheme != null) try { PowerPlan.Activate(_prevScheme); } catch { }
        if (_timerSet) try { NtSetTimerResolution(5000, false, out _); } catch { }
    }

    private static Process? ForegroundProcess()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        try { return Process.GetProcessById((int)pid); } catch { return null; }
    }

    [DllImport("ntdll.dll")] private static extern int NtSetTimerResolution(uint desired, bool set, out uint current);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}

/// <summary>Plano de energia ativo (powercfg).</summary>
public static class PowerPlan
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    /// <summary>Desempenho Máximo (Ultimate Performance): tira as micro-economias do Alto desempenho. Só em desktop.</summary>
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    // Configuração escondida "personalidade do plano": 0 economia, 1 desempenho (Alto e Máximo), 2 equilibrado.
    private const string SubNone = "fea3413e-7e05-4911-9a71-700331f1c294", Personality = "245d8541-3943-4422-b025-13a784f679b7";

    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    /// <summary>Melhor plano para jogar neste PC: Desempenho Máximo em desktop, Alto desempenho em notebook (o GUID modelo).</summary>
    public static string Best() => GamerAudit.Power().HasBattery ? HighPerformance : Ultimate;

    public static string? Active()
    {
        var m = GuidRx.Match(SecurityAudit.Run("powercfg.exe", "/getactivescheme", 10_000).Output);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Planos existentes: GUID e nome (o nome vem traduzido).</summary>
    public static List<(string Guid, string Name)> List()
    {
        var list = new List<(string, string)>();
        foreach (var line in SecurityAudit.Run("powercfg.exe", "/list", 10_000).Output.Split('\n'))
        {
            var m = Regex.Match(line, GuidRx + @"\s*\((.*?)\)");
            if (m.Success) list.Add((m.Value[..36].ToLowerInvariant(), m.Groups[1].Value.Trim()));
        }
        return list;
    }

    /// <summary>
    /// Acha o plano já existente do tipo pedido (pelo GUID original ou por uma cópia com outro GUID, reconhecida pelo nome).
    /// Muitos PCs têm o Desempenho Máximo com um GUID aleatório, criado por outro programa ou tutorial.
    /// </summary>
    private static string? Find(string template, List<(string Guid, string Name)> plans)
    {
        if (plans.FirstOrDefault(p => p.Guid == template).Guid is string g) return g;
        string rx = template == Ultimate ? @"ultimate|m.ximo|maximum" : @"high perf|alto desempenho";
        return plans.FirstOrDefault(p => Regex.IsMatch(p.Name, rx, RegexOptions.IgnoreCase)).Guid;
    }

    /// <summary>GUID do plano do tipo pedido, criando a cópia do modelo escondido se ainda não existir.</summary>
    private static string? Ensure(string template, List<string> log)
    {
        if (Find(template, List()) is string found) return found;
        // Sem destino: o Windows cria a cópia com um GUID novo e mostra esse GUID na saída.
        var (code, output) = SecurityAudit.Run("powercfg.exe", $"-duplicatescheme {template}", 15_000);
        var m = GuidRx.Matches(output).Select(x => x.Value.ToLowerInvariant()).FirstOrDefault(x => x != template);
        if (code == 0 && m != null) return m;
        if (Find(template, List()) is string after) return after;
        log.Add($"criar o plano: {output.Trim()}");
        return null;
    }

    /// <summary>O plano é de desempenho (Alto ou Máximo, inclusive cópias e planos de fabricante com essa personalidade)?</summary>
    public static bool IsGamingPlan(string? guid)
    {
        if (guid == null) return false;
        if (guid is HighPerformance or Ultimate) return true;
        var plans = List();
        if (guid == Find(Ultimate, plans) || guid == Find(HighPerformance, plans)) return true;
        var hex = GuidRx.Replace(SecurityAudit.Run("powercfg.exe", $"/qh {guid} {SubNone} {Personality}", 10_000).Output, "");
        var v = Regex.Matches(hex, @"0x([0-9a-fA-F]{8})");
        return v.Count >= 2 && Convert.ToInt32(v[^2].Groups[1].Value, 16) == 1;
    }

    /// <summary>Ativa o melhor plano possível (Máximo → Alto desempenho). Devolve "" ou o motivo, com a mensagem do Windows.</summary>
    public static string ActivateBest()
    {
        var log = new List<string>();
        var order = Best() == Ultimate ? new[] { Ultimate, HighPerformance } : new[] { HighPerformance };
        foreach (var template in order)
        {
            if (Ensure(template, log) is not string guid) continue;
            var (code, output) = SecurityAudit.Run("powercfg.exe", $"/setactive {guid}", 10_000);
            if (code == 0 && Active() == guid) return "";
            log.Add($"ativar {guid}: {output.Trim()} (código {code})");
        }
        // Último recurso: algum plano de desempenho que já exista (de fabricante, por exemplo).
        foreach (var (guid, _) in List())
            if (IsGamingPlan(guid) && SecurityAudit.Run("powercfg.exe", $"/setactive {guid}", 10_000).Code == 0 && Active() == guid) return "";
        string modern = !ModernStandby() ? ""
            : " Este PC usa o Modern Standby (suspensão moderna), em que o Windows só aceita o plano Equilibrado. " +
              (SetBestPerformanceOverlay() ? "No lugar dele, o tHg ligou o modo de energia \"Melhor desempenho\", que é o máximo permitido nesse caso."
                                           : "Use o modo de energia \"Melhor desempenho\" em Configurações → Sistema → Energia.");
        return "O Windows recusou o plano de desempenho." + modern + (log.Count > 0 ? " Detalhes: " + string.Join(" · ", log) : "");
    }

    public static bool Activate(string guid)
    {
        if (guid == Ultimate || guid == HighPerformance) return ActivateBest() == "";
        return SecurityAudit.Run("powercfg.exe", $"/setactive {guid}", 10_000).Code == 0;
    }

    /// <summary>Modern Standby (S0 Low Power Idle): planos além do Equilibrado ficam escondidos ou bloqueados.</summary>
    public static bool ModernStandby()
    {
        // Primeiro bloco da saída = estados disponíveis. No Modern Standby aparece o "S0" ali.
        string o = SecurityAudit.Run("powercfg.exe", "/a", 10_000).Output.Replace("\r", "");
        int cut = o.IndexOf("\n\n", StringComparison.Ordinal);
        return (cut > 0 ? o[..cut] : o).Contains("S0", StringComparison.Ordinal);
    }

    /// <summary>
    /// Modo de energia do Windows 11/10 (o controle deslizante "Melhor desempenho"), que vale também no Modern Standby.
    /// </summary>
    public static bool SetBestPerformanceOverlay()
    {
        var (code, _) = SecurityAudit.Run("powercfg.exe", "/overlaysetactive ded574b5-45a0-4f42-8737-46345c09c238", 10_000);
        return code == 0;
    }
}

/// <summary>
/// Topologia da CPU para o Modo Jogo: núcleos que rendem mais em jogos. Ryzen X3D de dois CCDs: o CCD com o L3 maior
/// (3D V-Cache). Intel híbrido (12ª geração em diante): os núcleos P (classe de eficiência mais alta).
/// Em CPUs comuns não há o que escolher e devolve null.
/// </summary>
public static unsafe class CpuTopology
{
    public static ulong? GameCoresMask(out string what)
    {
        what = "";
        uint len = 0;
        GetLogicalProcessorInformationEx(0xFFFF, null, ref len);
        if (len == 0) return null;
        byte* buf = (byte*)NativeMemory.Alloc(len);
        try
        {
            if (!GetLogicalProcessorInformationEx(0xFFFF, buf, ref len)) return null;
            var l3 = new List<(uint Size, ulong Mask)>();
            var cores = new List<(byte Eff, ulong Mask)>();
            for (uint off = 0; off < len;)
            {
                byte* p = buf + off;
                int rel = *(int*)p; uint size = *(uint*)(p + 4);
                if (rel == 0) // RelationProcessorCore: Flags@8, EfficiencyClass@9, GroupMask@32 (Mask, Group@40)
                {
                    ushort group = *(ushort*)(p + 40);
                    if (group == 0) cores.Add((*(p + 9), *(ulong*)(p + 32)));
                }
                else if (rel == 2 && *(p + 8) == 3) // RelationCache nível 3: CacheSize@12, GroupMask@40 (Mask, Group@48)
                {
                    ushort group = *(ushort*)(p + 48);
                    if (group == 0) l3.Add((*(uint*)(p + 12), *(ulong*)(p + 40)));
                }
                off += size;
                if (size == 0) break;
            }

            ulong all = cores.Aggregate(0UL, (a, c) => a | c.Mask);
            if (l3.Count > 1 && l3.Select(c => c.Size).Distinct().Count() > 1)
            {
                var big = l3.OrderByDescending(c => c.Size).First();
                what = $"núcleos com 3D V-Cache ({big.Size >> 20} MB de L3)";
                return big.Mask != all ? big.Mask : null;
            }
            if (cores.Select(c => c.Eff).Distinct().Count() > 1)
            {
                byte top = cores.Max(c => c.Eff);
                ulong mask = cores.Where(c => c.Eff == top).Aggregate(0UL, (a, c) => a | c.Mask);
                what = $"só núcleos P ({cores.Count(c => c.Eff == top)} núcleos)";
                return mask != all ? mask : null;
            }
            return null;
        }
        finally { NativeMemory.Free(buf); }
    }

    /// <summary>
    /// Núcleos preferidos por CPU Sets (SetProcessDefaultCpuSets): o Windows agenda o jogo nesses núcleos, mas ainda pode
    /// usar os outros se faltar. É o que o driver de 3D V-Cache da AMD e o Process Lasso usam. false = o Windows não aceitou.
    /// </summary>
    public static bool SetPreferredCores(System.Diagnostics.Process p, ulong mask)
    {
        uint len = 0;
        GetSystemCpuSetInformation(null, 0, ref len, IntPtr.Zero, 0);
        if (len == 0) return false;
        byte* buf = (byte*)NativeMemory.Alloc(len);
        try
        {
            if (!GetSystemCpuSetInformation(buf, len, ref len, IntPtr.Zero, 0)) return false;
            var ids = new List<uint>();
            for (uint off = 0; off < len;)
            {
                byte* e = buf + off;
                uint size = *(uint*)e;
                // SYSTEM_CPU_SET_INFORMATION: Size@0, Type@4, Id@8, Group@12, LogicalProcessorIndex@14
                if (*(int*)(e + 4) == 0 && *(ushort*)(e + 12) == 0)
                {
                    byte lp = *(e + 14);
                    if (lp < 64 && (mask & (1UL << lp)) != 0) ids.Add(*(uint*)(e + 8));
                }
                if (size == 0) break;
                off += size;
            }
            if (ids.Count == 0) return false;
            var arr = ids.ToArray();
            fixed (uint* a = arr) return SetProcessDefaultCpuSets(p.Handle, a, (uint)arr.Length);
        }
        catch { return false; }
        finally { NativeMemory.Free(buf); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(byte* info, uint bufferLength, ref uint returnedLength, IntPtr process, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(IntPtr process, uint* cpuSetIds, uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);
}

/// <summary>Caches de shaders do DirectX e dos drivers. Depois de atualizar o driver, caches velhos causam engasgos.</summary>
public static class ShaderCache
{
    public static List<string> Folders()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string localLow = Path.Combine(Path.GetDirectoryName(local)!, "LocalLow");
        return new[]
        {
            Path.Combine(local, "D3DSCache"),
            Path.Combine(local, @"NVIDIA\DXCache"), Path.Combine(local, @"NVIDIA\GLCache"),
            Path.Combine(localLow, @"NVIDIA\PerDriverVersion\DXCache"), Path.Combine(localLow, @"NVIDIA\PerDriverVersion\GLCache"),
            Path.Combine(local, @"AMD\DxCache"), Path.Combine(local, @"AMD\DxcCache"), Path.Combine(local, @"AMD\VkCache"), Path.Combine(local, @"AMD\GLCache"),
            Path.Combine(localLow, @"Intel\ShaderCache"),
        }.Where(Directory.Exists).ToList();
    }

    public static long Size() => Folders().Sum(d =>
    {
        try { return new DirectoryInfo(d).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); }
        catch { return 0L; }
    });

    /// <summary>Apaga o conteúdo das pastas (arquivos em uso ficam). Devolve os bytes liberados.</summary>
    public static long Clean()
    {
        long freed = 0;
        foreach (var d in Folders())
            foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                try { long l = f.Length; f.Delete(); freed += l; } catch { }
        return freed;
    }
}

/// <summary>
/// Como o ProBalance do Process Lasso: durante o jogo, um programa de fundo que passa de 10% da CPU por duas leituras
/// seguidas (6 s) vai para prioridade "abaixo do normal" e volta ao normal quando o jogo fecha. Não muda nada que já
/// esteja em outra prioridade, nem sistema, anti-cheat, áudio, gravação e transmissão (OBS), nem o próprio jogo.
/// </summary>
public sealed class BackgroundBalancer
{
    private static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "audiodg", "MsMpEng", "SecurityHealthService", "fontdrvhost", "ctfmon", "sihost", "ShellExperienceHost", "StartMenuExperienceHost",
        "tHgOptimization", "obs64", "obs32", "obs", "Streamlabs OBS", "XSplit.Core", "nvcontainer", "NVIDIA Share", "NVDisplay.Container",
        "RadeonSoftware", "AMDRSServ", "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceitservice",
        "Discord", "steam", "steamwebhelper", "EpicGamesLauncher", "RiotClientServices",
    };

    private readonly Dictionary<int, (TimeSpan Cpu, int Strikes)> _last = new();
    private readonly Dictionary<int, (System.Diagnostics.Process P, string Name)> _restrained = new();
    private DateTime _lastSample;

    public void Sample(int gamePid, Func<System.Diagnostics.Process, bool>? protect = null)
    {
        if ((DateTime.Now - _lastSample).TotalSeconds < 3) return;
        double secs = _lastSample == default ? 0 : (DateTime.Now - _lastSample).TotalSeconds;
        _lastSample = DateTime.Now;
        double capacity = Environment.ProcessorCount * secs * 1000; // ms de CPU disponíveis no intervalo
        var seen = new HashSet<int>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.Id == gamePid || p.Id <= 4 || Never.Contains(p.ProcessName) || _restrained.ContainsKey(p.Id)) { p.Dispose(); continue; }
                var cpu = p.TotalProcessorTime;
                seen.Add(p.Id);
                if (_last.TryGetValue(p.Id, out var prev) && capacity > 0)
                {
                    double share = (cpu - prev.Cpu).TotalMilliseconds / capacity;
                    int strikes = share > 0.10 ? prev.Strikes + 1 : 0;
                    _last[p.Id] = (cpu, strikes);
                    if (strikes >= 2 && p.PriorityClass == System.Diagnostics.ProcessPriorityClass.Normal && protect?.Invoke(p) != true)
                    {
                        p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                        ProcessIo.SetIoPriority(p, 1);      // disco: baixa
                        ProcessIo.SetPagePriority(p, 2);    // memória: baixa (sai primeiro da RAM se faltar)
                        _restrained[p.Id] = (p, p.ProcessName);
                        continue; // mantém o objeto para restaurar depois
                    }
                }
                else _last[p.Id] = (cpu, 0);
                p.Dispose();
            }
            catch { try { p.Dispose(); } catch { } }
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
    }

    /// <summary>Devolve a prioridade normal a todos que foram rebaixados. Retorna quantos eram.</summary>
    public int RestoreAll()
    {
        int n = _restrained.Count;
        foreach (var (p, _) in _restrained.Values)
        {
            try
            {
                if (!p.HasExited && p.PriorityClass == System.Diagnostics.ProcessPriorityClass.BelowNormal) p.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                if (!p.HasExited) { ProcessIo.SetIoPriority(p, 2); ProcessIo.SetPagePriority(p, 5); }
            }
            catch { }
            p.Dispose();
        }
        _restrained.Clear(); _last.Clear(); _lastSample = default;
        return n;
    }
}

/// <summary>
/// EcoQoS (modo de eficiência do Gerenciador de Tarefas) nos programas de fundo pesados enquanto o jogo roda: navegadores,
/// launchers e Discord passam a rodar com clock baixo e nos núcleos econômicos (híbridos), sobrando CPU e energia para o jogo.
/// Quando o jogo fecha, cada um volta para o controle normal do Windows. Os filhos (abas do navegador, webhelpers) entram
/// pelo nome do executável; processos da pasta do jogo nunca entram.
/// </summary>
public sealed class BackgroundEcoQos
{
    private static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "EADesktop", "EABackgroundService",
        "upc", "UbisoftConnect", "GalaxyClient", "GalaxyClient Helper", "Discord",
    };

    private readonly Dictionary<int, System.Diagnostics.Process> _applied = new();
    private DateTime _lastSample;

    /// <summary>Aplica o EcoQoS nos alvos ainda não marcados (a cada 10 s no máximo). Retorna quantos entraram agora.</summary>
    public int Sample(int gamePid, Func<System.Diagnostics.Process, bool>? protect = null, bool force = false)
    {
        if (!force && (DateTime.Now - _lastSample).TotalSeconds < 10) return 0;
        _lastSample = DateTime.Now;
        int n = 0;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.Id == gamePid || _applied.ContainsKey(p.Id) || !Targets.Contains(p.ProcessName) || protect?.Invoke(p) == true) { p.Dispose(); continue; }
                if (PowerThrottling.SetEco(p, true)) { _applied[p.Id] = p; n++; continue; }
                p.Dispose();
            }
            catch { try { p.Dispose(); } catch { } }
        }
        return n;
    }

    /// <summary>Devolve todos ao controle automático do Windows. Retorna quantos eram.</summary>
    public int RestoreAll()
    {
        int n = _applied.Count;
        foreach (var p in _applied.Values)
        {
            try { if (!p.HasExited) PowerThrottling.Reset(p); } catch { }
            p.Dispose();
        }
        _applied.Clear(); _lastSample = default;
        return n;
    }
}

/// <summary>
/// SetProcessInformation(ProcessPowerThrottling): ControlMask diz quais políticas o processo controla e StateMask liga cada uma.
/// EXECUTION_SPEED ligado = EcoQoS; controlado e desligado = HighQoS (nunca throttled); ControlMask 0 = o Windows decide.
/// </summary>
public static class PowerThrottling
{
    private const uint ExecutionSpeed = 0x1; // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    public static bool SetEco(System.Diagnostics.Process p, bool eco) => Set(p, ExecutionSpeed, eco ? ExecutionSpeed : 0);
    public static bool Reset(System.Diagnostics.Process p) => Set(p, 0, 0);

    private static bool Set(System.Diagnostics.Process p, uint control, uint state)
    {
        try
        {
            var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = control, StateMask = state };
            return SetProcessInformation(p.Handle, 4 /* ProcessPowerThrottling */, ref st, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);
}

/// <summary>
/// Notificações do Windows (a mesma chave do interruptor "Notificações" em Configurações → Sistema): desligadas enquanto o
/// jogo roda e devolvidas ao fechar. O valor de antes fica salvo nas configurações, então volta mesmo se o tHg fechar no meio.
/// </summary>
public static class Notifications
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string Name = "ToastEnabled";

    public static bool Mute(AppSettings s)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            int cur = k.GetValue(Name) is int v ? v : 1;
            if (cur == 0) return false; // já estavam desligadas pelo usuário
            s.QuietRestore ??= cur;
            s.Save();
            k.SetValue(Name, 0, Microsoft.Win32.RegistryValueKind.DWord);
            Broadcast();
            return true;
        }
        catch { return false; }
    }

    public static void Restore(AppSettings s)
    {
        if (s.QuietRestore is not int prev) return;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue(Name, prev, Microsoft.Win32.RegistryValueKind.DWord);
            Broadcast();
        }
        catch { }
        s.QuietRestore = null;
        s.Save();
    }

    /// <summary>Avisa o Windows que a configuração mudou (WM_SETTINGCHANGE), sem esperar quem não responde.</summary>
    private static void Broadcast()
    {
        try { SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "Policy", 0x0002, 1000, out _); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);
}

/// <summary>
/// Pausa (como o Process Lasso / Process Explorer) programas pesados durante o jogo: ficam congelados, sem usar CPU,
/// e voltam exatamente de onde pararam quando o jogo fecha. Nunca pausa o próprio jogo, processos do sistema,
/// anti-cheats, Discord, OBS ou o tHg. Os PIDs pausados ficam salvos, e o tHg retoma na próxima abertura se fechar no meio.
/// </summary>
public static class ProcessPause
{
    private static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "csrss", "winlogon", "wininit", "services", "lsass", "svchost", "dwm", "explorer", "audiodg", "MsMpEng",
        "tHgOptimization", "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceitservice",
        "Discord", "obs64", "obs32", "steam", "steamwebhelper", "RiotClientServices",
    };

    public static int Pause(AppSettings s, int gamePid)
    {
        var names = s.GameModeSuspendList.Split(',', ';').Select(n => n.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
                     .Where(n => n.Length > 0 && !Never.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == gamePid || p.Id == Environment.ProcessId || !names.Contains(p.ProcessName)) continue;
                if (NtSuspendProcess(p.Handle) == 0) { s.SuspendedPids.Add(p.Id); n++; }
            }
            catch { }
            finally { p.Dispose(); }
        }
        if (n > 0) s.Save();
        return n;
    }

    public static void ResumeAll(AppSettings s)
    {
        if (s.SuspendedPids.Count == 0) return;
        foreach (int pid in s.SuspendedPids.ToList())
            try { using var p = Process.GetProcessById(pid); NtResumeProcess(p.Handle); } catch { }
        s.SuspendedPids.Clear();
        s.Save();
    }

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
}

/// <summary>
/// Prioridade de disco (I/O) e de memória por processo (NtSetInformationProcess): o que o Process Lasso chama de
/// "I/O priority" e "memory priority". Disco: 0 muito baixa, 1 baixa, 2 normal, 3 alta. Memória: 1 a 5 (5 = normal, o máximo).
/// </summary>
public static class ProcessIo
{
    public static bool SetIoPriority(Process p, int level)
    {
        try { int v = Math.Clamp(level, 0, 3); return NtSetInformationProcess(p.Handle, 33 /* ProcessIoPriority */, ref v, 4) == 0; }
        catch { return false; }
    }

    public static bool SetPagePriority(Process p, int level)
    {
        try { int v = Math.Clamp(level, 1, 5); return NtSetInformationProcess(p.Handle, 39 /* ProcessPagePriority */, ref v, 4) == 0; }
        catch { return false; }
    }

    [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int length);
}

/// <summary>SysMain (Superfetch) e Windows Search parados durante o jogo e religados ao fechar; a lista fica salva.</summary>
public static class GameServices
{
    private static readonly string[] Names = { "SysMain", "WSearch" };

    public static int Stop(AppSettings s)
    {
        int n = 0;
        foreach (var name in Names)
        {
            var (code, output) = SecurityAudit.Run("sc.exe", $"query {name}", 10_000);
            if (code != 0 || !output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) && !output.Contains(" 4 ", StringComparison.Ordinal)) continue;
            if (SecurityAudit.Run("sc.exe", $"stop {name}", 20_000).Code == 0) { s.StoppedServices.Add(name); n++; }
        }
        if (n > 0) s.Save();
        return n;
    }

    public static void Restart(AppSettings s)
    {
        if (s.StoppedServices.Count == 0) return;
        foreach (var name in s.StoppedServices.Where(n => Names.Contains(n)).ToList())
            SecurityAudit.Run("sc.exe", $"start {name}", 20_000);
        s.StoppedServices.Clear();
        s.Save();
    }
}
