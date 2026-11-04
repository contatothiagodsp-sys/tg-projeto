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

    // volatile: TickCore escreve na tarefa de fundo e a janela lê sem trava (Active, ActiveProfile, ActiveGamePid).
    private volatile Process? _game;
    private string _gameName = "";
    private DateTime _start;
    // Ficam nas configurações (não só na memória): um crash no meio do jogo não deixa o PC preso no alto desempenho.
    private string? _prevScheme { get => _s.GameModePrevScheme; set { _s.GameModePrevScheme = value; _s.Save(); } }
    private string? _prevOverlay { get => _s.GameModePrevOverlay; set { _s.GameModePrevOverlay = value; _s.Save(); } }
    private readonly Infrastructure.ITimerResolutionLease _timer = new Infrastructure.WindowsTimerResolutionLease();
    private Infrastructure.CpuSetPlan? _plan;
    // Tick roda numa tarefa de fundo (powercfg/sc.exe não travam a janela); Dispose espera o Tick em andamento.
    private readonly object _sync = new();
    private readonly BackgroundBalancer _balancer = new();
    private readonly BackgroundEcoQos _eco = new();
    private readonly BackgroundCores _cores;
    private readonly ClientDemoter _clients;
    private AutoProfile _profile;
    private DateTime _lastPurge;
    private int _purges;
    private double _fpsSum, _lowSum;
    private int _samples;
    // RAM disponível ao longo da sessão, a cada tick: é a única causa de engasgo que os dados daqui conseguem associar. Limite de 3 h.
    private const int MaxMemorySamples = 3600;
    private readonly List<(DateTime At, double AvailablePercent)> _memorySamples = new();

    public string Status { get; private set; } = "Desligado";
    public bool Active => _game != null;
    /// <summary>Perfil automático da sessão em andamento (null = nenhum jogo aberto).</summary>
    public AutoProfile? ActiveProfile => _game != null ? _profile : null;
    /// <summary>PID do jogo da sessão em andamento (null = nenhum jogo aberto). Para o painel: não abre o processo.</summary>
    public int? ActiveGamePid => _game?.Id;
    /// <summary>Quando a sessão em andamento começou.</summary>
    public DateTime ActiveSince => _start;
    public string CpuName { get; set; } = "";

    /// <summary>Linha do tempo da sessão em andamento. volatile: Begin e End rodam na tarefa de fundo, e a amostra vem da janela.</summary>
    private volatile Core.Benchmark.SessionRecorder? _session;
    private long _wheaAtStart = -1, _wheaNow;
    private int _sampleCount;

    /// <summary>
    /// Uma amostra por segundo, chamada pela janela, que é a dona das linhas de sensor. Só com sessão aberta. Erros de hardware: a cada 5 s, em segundo plano, porque ler o registro
    /// de eventos não pode pesar no tique. FrameStats vem da sessão ETW, sem abrir o processo do jogo (Vanguard-safe).
    /// </summary>
    public void SampleSession(SensorMonitor monitor, FrameMonitor frames)
    {
        if (_session is not { } session || ActiveGamePid is not int pid) return;
        if (_sampleCount++ % 5 == 0)
        {
            var since = _start;
            _ = Task.Run(() =>
            {
                var e = OcCheck.ErrorsSince(since);
                if (e.Error == null) Volatile.Write(ref _wheaNow, e.Whea);
            });
        }
        long whea = Volatile.Read(ref _wheaNow);
        bool newError = whea > _wheaAtStart && _wheaAtStart >= 0;
        _wheaAtStart = whea;
        bool hot = monitor.CpuTemperature >= 95 || monitor.GpuTemperature >= 90; // perto do limite comum de corte; o gráfico diz "temperatura alta", sem afirmar o corte
        session.Sample(monitor, frames.GetStats(pid), stuttersThisSecond: 0, hot, newError);
    }

    public GameModeEngine(AppSettings s)
    {
        _s = s;
        _cores = new BackgroundCores(s);
        _clients = new ClientDemoter(s);
        // Sobrou de uma sessão interrompida (crash, PC desligado no meio do jogo): devolve o plano de antes, fora da janela.
        if (s.GameModePrevScheme != null || s.GameModePrevOverlay != null || s.Read(x => x.BgCorePids.Count > 0 || x.ClientDemotedPids.Count > 0))
            Task.Run(() => { lock (_sync) if (_game == null) DisposeCore(); });
    }

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
        lock (_sync)
        {
            try { TickCore(frames); }
            finally
            {
                // Uma gravação por tick, mesmo com vários alvos: o JSON inteiro seria serializado para cada amostrador.
                if (_dirty)
                {
                    _dirty = false;
                    try { _s.Save(); } catch (Exception ex) { Infrastructure.AppLog.Warn("modo jogo", ex); }
                }
            }
        }
    }

    /// <summary>Há PIDs novos para gravar (BgCorePids, ClientDemotedPids). Só é acessado dentro de <see cref="Tick"/>, sob <c>_sync</c>: não precisa de volatile.</summary>
    private bool _dirty;

    private void TickCore(FrameMonitor frames)
    {
        _fm = frames;
        if (_game != null)
        {
            if (GameExited()) { End(); return; }
            var stats = frames.GetStats(_game.Id); // uma leitura por tick: cada uma disputa o lock com o evento de frame
            if (stats is { Fps: > 1 } f) { _fpsSum += f.Fps; _lowSum += f.Low1Fps; _samples++; }
            WatchStandby(stats);
            SampleMemory();
            // Uma leitura da lista por tick, e só se alguém for usá-la: cada amostrador lia a sua (até 4 por tick).
            bool balance = _s.GameModeProBalance && _balancer.Due, eco = EcoOn && _eco.Due, cores = BgOn && _cores.Due, clients = _clients.Due;
            if (balance || eco || cores || clients)
            {
                var snap = GetSnapshot();
                if (balance) _balancer.Sample(snap, _game.Id, IsGameFamily);
                if (eco) _eco.Sample(snap, _game.Id, IsGameFamily); // navegadores e launchers abertos depois do jogo também
                if (cores) { _cores.Sample(snap, _game.Id, IsGameFamily); _dirty |= _cores.LastAppliedCount > 0; }
                if (clients) { _clients.Sample(snap); _dirty |= _clients.LastAppliedCount > 0; } // cliente do LoL/Riot reaberto ou que demorou a subir
            }
            return;
        }
        if (!_s.AutoGameMode) { Status = "Desligado"; return; }
        Status = "Aguardando um jogo abrir…";

        if (ForegroundPid() is not int pid) return;
        string? process = ForegroundName(pid);
        if (process == null || !IsGame(pid, process, out string name)) return;
        // Só agora existe um objeto Process: trocar de janela entre programas comuns não aloca nem abre nada.
        if (OpenGame(pid, process) is Process game) Begin(game, name);
    }

    private void SampleMemory()
    {
        if (_memorySamples.Count >= MaxMemorySamples) return;
        var (total, available) = Optimizer.MemoryStatus();
        if (total > 0) _memorySamples.Add((DateTime.Now, available * 100.0 / total));
    }

    /// <summary>Converte para o relógio dos frames. A precisão é a do tick (3 s), e o classificador considera isso.</summary>
    private List<MemorySample> MemorySamplesFor(List<double> times)
    {
        var list = new List<MemorySample>(_memorySamples.Count);
        if (times.Count == 0) return list;
        foreach (var (at, percent) in _memorySamples) list.Add(new MemorySample(times[0] + (at - _start).TotalMilliseconds, percent));
        return list;
    }

    private List<Infrastructure.ProcessSnapshot>? _snapshotCache;
    private DateTime _snapshotCacheAt;

    /// <summary>Lista de processos do kernel, reaproveitada por 1 s. Lista vazia (o Windows não respondeu) não fica guardada.</summary>
    private List<Infrastructure.ProcessSnapshot> GetSnapshot()
    {
        if (_snapshotCache is { Count: > 0 } && (DateTime.Now - _snapshotCacheAt).TotalSeconds < 1) return _snapshotCache;
        _snapshotCache = Infrastructure.ProcessUtil.Snapshot();
        _snapshotCacheAt = DateTime.Now;
        return _snapshotCache;
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

    // Valorant e LoL: EcoQoS e threads de fundo fazem parte do perfil, mesmo com as opções desligadas.
    private bool EcoOn => _s.GameModeEcoQos || _profile is AutoProfile.Valorant or AutoProfile.Lol;
    private bool BgOn => _s.GameModeBgCores || _profile == AutoProfile.Valorant;

    /// <summary>
    /// O jogo fechou? Valorant (Vanguard-safe): pelo nome do PID, em O(1) e sem abrir o processo do jogo
    /// (Process.HasExited abre um handle). Os demais: HasExited.
    /// </summary>
    private bool GameExited()
    {
        if (_game == null) return true;
        if (_profile == AutoProfile.Valorant)
        {
            string? name = Infrastructure.ProcessUtil.NameOf(_game.Id);
            if (!ValorantExited(name, _game.ProcessName)) return false;
            // Sem nome, confirma na lista: lista vazia é o Windows sem responder, e encerrar a sessão por isso seria pior que esperar.
            return name != null || Infrastructure.ProcessUtil.HasExited(Infrastructure.ProcessUtil.Snapshot(), _game.Id, _game.ProcessName);
        }
        try { return _game.HasExited; } catch { return true; }
    }

    /// <summary>Sem nome, o PID não existe mais; com outro nome, foi reaproveitado por outro programa.</summary>
    internal static bool ValorantExited(string? currentName, string gameName) =>
        currentName == null || !currentName.Equals(gameName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Processo do mesmo jogo (mesma pasta de instalação ou exe conhecido): o ProBalance nunca rebaixa.</summary>
    internal bool IsGameFamily(Process p)
    {
        if (CompetitiveProfiles.IsVanguardProtected(p.ProcessName)) return true; // nem o caminho é lido
        if (_libraryExes.Contains(p.ProcessName + ".exe")) return true;
        if (_gameDir == null) return false;
        return Infrastructure.ProcessUtil.ImagePath(p)?.StartsWith(_gameDir, StringComparison.OrdinalIgnoreCase) == true;
    }

    private string? _gameDir;

    private bool IsGame(int pid, string process, out string name)
    {
        name = "";
        if (NotAGame.IsMatch(process)) return false;
        // Vanguard-safe: o Valorant é reconhecido só pelo nome, sem abrir o processo para ler o caminho.
        if (CompetitiveProfiles.IsVanguardProtected(process))
        {
            if (CompetitiveProfiles.Detect(process) != AutoProfile.Valorant) return false; // VALORANT.exe (inicializador), vgtray…
            name = NvDriverProfiles.ByExe(CompetitiveProfiles.ValorantExe)?.Name ?? "Valorant";
            return true;
        }
        string? path;
        path = Infrastructure.ProcessUtil.ImagePath(pid); // sem ler a memória do jogo (anti-cheats registram quem abre o processo com VM_READ)
        string exe = path != null ? Path.GetFileName(path) : process + ".exe";
        if (NvDriverProfiles.ByExe(exe) is { } preset) { name = preset.Name; return true; }
        if (path == null) return false;
        var lib = _library.FirstOrDefault(g => g.InstallDir.Length > 3 && path.StartsWith(g.InstallDir, StringComparison.OrdinalIgnoreCase))
                  ?? _library.FirstOrDefault(g => g.ExePath != null && path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase));
        if (lib == null) return false;
        name = lib.Name;
        return true;
    }

    /// <summary>
    /// O jogo ganha prioridade de disco alta já nesta sessão? Valorant nunca (o processo não é tocado), LoL não (o perfil
    /// dele não mexe em prioridade) e nem quem o perfil da Biblioteca deixou em prioridade normal.
    /// </summary>
    internal static bool RaisesIo(AutoProfile profile, ProcessPriorityClass priority) =>
        profile is AutoProfile.Cs2 or AutoProfile.Universal && priority != ProcessPriorityClass.Normal;

    private bool _gameIoRaised;
    private ProcessPriorityClass? _gamePriorityBefore, _gamePriorityApplied;

    /// <summary>
    /// Devolve a prioridade de antes da sessão. Se ela não é mais a que o tHg pôs, foi o usuário ou o jogo que mudou, e essa decisão fica.
    /// Devolve true se restaurou.
    /// </summary>
    internal static bool RestorePriority(Process game, ProcessPriorityClass? before, ProcessPriorityClass? applied)
    {
        if (before is not ProcessPriorityClass original || applied is not ProcessPriorityClass ours || original == ours) return false;
        if (game.HasExited) return false;
        game.Refresh(); // Process guarda a última leitura: sem isto, uma mudança feita de fora não apareceria
        if (game.PriorityClass != ours) return false;
        game.PriorityClass = original;
        return true;
    }

    /// <summary>Se o jogo ainda está aberto (o tHg fechou ou a sessão acabou antes dele), devolve prioridade, EcoQoS e disco ao normal.</summary>
    private void RestoreGameProcess()
    {
        bool raised = _gameIoRaised;
        _gameIoRaised = false;
        var before = _gamePriorityBefore;
        var applied = _gamePriorityApplied;
        _gamePriorityBefore = _gamePriorityApplied = null;
        if (_game == null || _profile == AutoProfile.Valorant) return;
        try
        {
            if (_game.HasExited) return;
            RestorePriority(_game, before, applied);
            PowerThrottling.Reset(_game.Id);
            if (raised) ProcessIo.SetIoPriority(_game.Id, 2);
        }
        catch (Exception ex) { Infrastructure.AppLog.Error("modo jogo", ex); }
    }

    private void Begin(Process p, string name)
    {
        _game = p; _gameName = name; _start = DateTime.Now; _fpsSum = _lowSum = 0; _samples = 0; _memorySamples.Clear();
        Restore(() =>
        {
            _session?.Dispose();
            _wheaAtStart = -1;
            Volatile.Write(ref _wheaNow, 0);
            _sampleCount = 0;
            var recorder = new Core.Benchmark.SessionRecorder(Core.Benchmark.SessionStore.Default);
            recorder.Begin(name);
            _session = recorder;
        });
        _profile = CompetitiveProfiles.Detect(p.ProcessName);
        bool vanguard = _profile == AutoProfile.Valorant; // nada abre o processo do jogo nesta sessão
        string? path = vanguard ? null : Infrastructure.ProcessUtil.ImagePath(p);
        try { _gameDir = path != null ? Path.GetDirectoryName(Path.GetDirectoryName(path)) : null; } catch { _gameDir = null; }
        if (_gameDir is { Length: <= 3 }) _gameDir = null; // nunca a raiz de um disco
        var done = new List<string> { $"perfil {CompetitiveProfiles.Label(_profile)}" };

        // Prioridade: a do perfil do jogo (Biblioteca), ou "acima do normal". "Alta" só se o usuário escolheu no perfil.
        var profile = vanguard ? null : ProfileFor(p);
        var prio = profile?.Priority switch { "high" => ProcessPriorityClass.High, "normal" => ProcessPriorityClass.Normal, _ => ProcessPriorityClass.AboveNormal };
        if (!vanguard)
            try
            {
                var before = p.PriorityClass; // antes de mudar: é o que volta no fim da sessão
                p.PriorityClass = prio;
                _gamePriorityBefore = before;
                _gamePriorityApplied = prio;
                done.Add(prio == ProcessPriorityClass.High ? "prioridade alta" : prio == ProcessPriorityClass.Normal ? "prioridade normal" : "prioridade acima do normal");
            }
            catch (Exception ex) { Infrastructure.AppLog.Error("modo jogo", ex); }

        // O registro abaixo só vale na próxima abertura: o disco já fica em prioridade alta nesta sessão.
        if (RaisesIo(_profile, prio) && ProcessIo.SetIoPriority(p.Id, CompetitiveProfiles.IoHigh))
        {
            _gameIoRaised = true;
            done.Add("prioridade de disco alta");
        }

        // Registro do Windows (IFEO PerfOptions): vale a partir da próxima vez que o jogo abrir, e é o único jeito de
        // dar prioridade ao Valorant sem tocar no processo. Universal: Alta só se o perfil da Biblioteca pede; senão acima do normal.
        string exe = vanguard ? CompetitiveProfiles.ValorantExe : Path.GetFileName(path ?? p.ProcessName + ".exe");
        int? ifeoCpu = _profile switch
        {
            AutoProfile.Cs2 or AutoProfile.Valorant => CompetitiveProfiles.CpuHigh,
            AutoProfile.Lol => null,
            _ => profile?.Priority switch { "high" => CompetitiveProfiles.CpuHigh, "normal" => null, _ => CompetitiveProfiles.CpuAboveNormal },
        };
        if (_profile != AutoProfile.Lol && CompetitiveProfiles.EnsurePerfOptions(_s, exe, ifeoCpu, CompetitiveProfiles.IoHigh))
            done.Add("prioridade de CPU e disco gravada no Windows (vale na próxima abertura)");
        if (_profile == AutoProfile.Cs2 && path != null && CompetitiveProfiles.EnsureNoFso(_s, path))
            done.Add("sem otimizações de tela cheia (vale na próxima abertura)");
        if (vanguard) done.Add("Vanguard-safe: o processo do jogo não é tocado");

        // O plano vem da topologia, nunca do nome da CPU nem do jogo. Sem restrição comprovada, o processo do jogo nem é aberto.
        // Sem fallback para a afinidade: presa num conjunto fixo, a thread do jogo espera mesmo com outros núcleos livres.
        _plan = Infrastructure.CpuSetPlanner.Plan(Infrastructure.CpuTopologySnapshot.Current,
            new Infrastructure.CpuPlanOptions(_s.GameModeAffinity, _s.GameModeAvoidCpu0, BgOn || _profile == AutoProfile.Lol));
        Infrastructure.AppLog.Info("modo jogo", $"{Infrastructure.CpuTopologySnapshot.Current.Summary}. {_plan.Reason}");
        _cores.UsePlan(_plan);
        _clients.UsePlan(_plan);
        if (!vanguard && _plan.RestrictsGame && CpuTopology.SetPreferredCores(p, _plan.GameMask))
            done.Add($"{_plan.GamePreferredCpuSets.Count} threads preferidos para o jogo");

        // Reserva o limite inteiro da gravação (24 MB): a sessão pode durar horas e a lista nunca cresce no meio do jogo.
        _captureId = _fm?.StartCapture(p.Id, TimeSpan.FromHours(3), 300) ?? 0;

        // Modern Standby: o Windows só aceita o Equilibrado; vai direto ao modo "Melhor desempenho" e devolve o anterior no fim.
        bool x3dParking = WindowsTweaks.IsDualCcdX3D(CpuName) || Infrastructure.CpuTopologySnapshot.Current.HasAsymmetricCache; // o driver da AMD depende do plano Equilibrado
        if (_s.GameModePowerPlan && !x3dParking && PowerPlan.IsModernStandby)
            try
            {
                string prev = PowerPlan.ActiveOverlay();
                if (prev != PowerPlan.BestPerformanceOverlay && PowerPlan.SetBestPerformanceOverlay()) { _prevOverlay = prev; done.Add("modo de energia Melhor desempenho"); }
            }
            catch (Exception ex) { Infrastructure.AppLog.Warn("modo jogo", ex); }
        else if (_s.GameModePowerPlan && !x3dParking)
            try
            {
                string? cur = PowerPlan.Active();
                if (cur != null && !PowerPlan.IsGamingPlan(cur) && PowerPlan.ActivateBest() == "")
                {
                    _prevScheme = cur; done.Add("alto desempenho");
                }
            }
            catch (Exception ex) { Infrastructure.AppLog.Warn("modo jogo", ex); }

        if (_s.GameModeCloseApps)
            try
            {
                var r = Optimizer.CloseApps(Optimizer.ListApps().Where(a => a.Suggested));
                if (r.Closed.Count > 0) done.Add($"fechados: {string.Join(", ", r.Closed)}");
            }
            catch (Exception ex) { Infrastructure.AppLog.Warn("modo jogo", ex); }

        // Na hora em que o jogo abre não se mexe na memória de ninguém: enxugar processos (inclusive o DWM, que desenha a tela)
        // e esvaziar o cache que o jogo acabou de ler causava engasgos e até o jogo minimizar. Só com a RAM quase cheia
        // (menos de 15% livre) esvazia a memória em espera.
        if (_s.GameModeCleanRam)
            try
            {
                var (total, avail) = Optimizer.MemoryStatus();
                if (total > 0 && avail < total * 0.15 && Optimizer.PurgeStandby()) done.Add("memória em espera liberada (RAM quase cheia)");
            }
            catch (Exception ex) { Infrastructure.AppLog.Warn("modo jogo", ex); }

        // Timer do Windows pedido uma vez por sessão, dentro do limite que o Windows informa. O efeito varia por PC e por jogo.
        // No Windows 11 só vale para o jogo com o ajuste "Resolução de timer… vale sempre" do OTIMIZA ligado.
        bool timerOn = _s.Read(x => Core.Performance.TimerMeasurement.TimerAllowed(x.PendingTimerMeasurement, name, x.GameProfiles.GetValueOrDefault(ProfileKey(name))?.TimerResolution));
        if (_s.GameModeTimer && timerOn && _timer.Acquire(Infrastructure.NativePerfApi.HalfMs) is uint timerNow) done.Add($"timer em {timerNow / 10000.0:0.0##} ms");
        else if (_s.GameModeTimer && !timerOn) done.Add("sem o timer de 0,5 ms neste jogo");

        if (_s.GameModeQuiet && Notifications.Mute(_s)) done.Add("notificações silenciadas");

        // Monitor na taxa máxima (alguns jogos e trocas de resolução deixam em 60 Hz).
        if (_s.GameModeMaxRefresh)
            try
            {
                foreach (var d in DisplayTuner.List().Where(d => d.BelowMax))
                    if (DisplayTuner.SetMaxRefresh(d, _s) is { Length: 0 }) done.Add($"monitor em {d.MaxHz} Hz");
            }
            catch (Exception ex) { Infrastructure.AppLog.Warn("modo jogo", ex); }


        // O jogo fica explicitamente fora do EcoQoS (o Windows 11 nunca o joga nos núcleos econômicos nem baixa o clock dele);
        // navegadores, launchers e Discord abertos em segundo plano entram no modo de eficiência até o jogo fechar.
        if (!vanguard) PowerThrottling.SetEco(p.Id, false);
        // O próprio tHg (sensores, telas LCD, contagem de FPS) também vai para o modo de eficiência: sai do caminho do jogo.
        if (EcoOn) using (var self = Process.GetCurrentProcess()) PowerThrottling.SetEco(self, true);
        if (EcoOn && _eco.Sample(GetSnapshot(), p.Id, IsGameFamily, force: true) is int eco and > 0) done.Add($"{eco} processo(s) de fundo em modo de eficiência");
        if (BgOn && _cores.Sample(GetSnapshot(), p.Id, IsGameFamily, force: true) is int moved and > 0) { _dirty = true; done.Add($"{moved} processo(s) de fundo em {_plan.BackgroundCpuSets.Count} threads separados"); }
        // Clientes da Riot/LoL: 2 últimos threads, EcoQoS e prioridade baixa até a partida acabar.
        _clients.SetTargets(_profile switch { AutoProfile.Lol => ClientDemoter.LolClients, AutoProfile.Valorant => ClientDemoter.RiotClients, _ => Array.Empty<string>() });
        if (_clients.HasTargets && _clients.Sample(GetSnapshot(), force: true) is int cl and > 0) { _dirty = true; done.Add($"{cl} processo(s) do cliente da Riot fora do caminho"); }

        // SysMain e Windows Search ficam parados durante o jogo (leem e indexam o disco em segundo plano).
        if (_s.GameModeServices && GameServices.Stop(_s) is int stopped and > 0) done.Add($"{stopped} serviço(s) de disco pausado(s)");

        // Programas pesados escolhidos pelo usuário ficam pausados (não fechados) até o jogo fechar.
        if (_s.GameModeSuspend && ProcessPause.Pause(_s, p.Id) is int paused and > 0) done.Add($"{paused} programa(s) pausado(s)");

        Status = $"ATIVO · {name}";
        Notify?.Invoke($"Modo Jogo · {name}", done.Count > 0 ? string.Join(" · ", done) : "Monitorando a sessão");
        // Uma vez por sessão, porque Begin roda uma vez por sessão. Fora dos X3D de dois CCDs, Check nem consulta o Windows.
        Restore(() =>
        {
            if (new Core.Performance.X3dGuardian.SessionNotice().Next(Core.Performance.X3dGuardian.Check(CpuName)) is { } x3dProblem)
                Notify?.Invoke("Jogo no CCD com 3D V-Cache", x3dProblem);
        });
    }

    /// <summary>
    /// Como o ISLC, mas só em aperto de verdade: o Windows reaproveita a memória em espera sem custo, e esvaziá-la à toa
    /// faz o jogo e o sistema lerem de novo do disco (engasgo). Só esvazia se a memória livre cair abaixo de 256 MB com
    /// mais de 1 GB em espera, no máximo a cada 60 s. Com o jogo acima de 60 FPS não esvazia: é partida em andamento, e o
    /// Windows reaproveita essa memória sozinho; o esvaziamento fica para menus e telas de carregamento.
    /// </summary>
    private void WatchStandby(FrameStats? frames)
    {
        if (!_s.GameModeCleanRam || (DateTime.Now - _lastPurge).TotalSeconds < 60) return;
        if (frames is { Fps: > 60 }) return;
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
        path = Infrastructure.ProcessUtil.ImagePath(p);
        if (path == null) return null;
        var lib = _library.FirstOrDefault(g => g.ExePath != null && g.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                  ?? _library.FirstOrDefault(g => g.InstallDir.Length > 3 && path.StartsWith(g.InstallDir, StringComparison.OrdinalIgnoreCase));
        return lib != null ? _s.Read(x => x.GameProfiles.GetValueOrDefault(lib.Key)) : null;
    }

    /// <summary>Uma etapa do fim da sessão que falha não pode impedir as outras nem deixar a sessão "aberta" para sempre.</summary>
    private static void Restore(Action step)
    {
        try { step(); }
        catch (Exception ex) { Infrastructure.AppLog.Error("modo jogo", ex); }
    }

    private void End()
    {
        try { EndCore(); }
        finally
        {
            var finished = _game;
            _game = null;
            finished?.Dispose();
            _fgPid = 0; _fgName = null;
            _snapshotCache = null;
            _plan = null;
            Status = _s.AutoGameMode ? "Aguardando um jogo abrir…" : "Desligado";
        }
    }

    private void EndCore()
    {
        _timer.Release();
        Restore(() => _clients.RestoreAll()); // primeiro: a tela pós-partida do LoL volta a ter a CPU toda na hora
        int restrained = 0;
        Restore(() => restrained = _balancer.RestoreAll());
        Restore(() => _eco.RestoreAll());
        Restore(() => _cores.RestoreAll());
        ResetSelf();
        Restore(RestoreGameProcess);
        Restore(() => Notifications.Restore(_s));
        Restore(() => ProcessPause.ResumeAll(_s));
        Restore(() => GameServices.Restart(_s));
        long dropped = 0;
        var times = _captureId != 0 && _fm != null ? _fm.StopCapture(_captureId, out dropped) : new List<double>();
        _captureId = 0;
        double minutes = (DateTime.Now - _start).TotalMinutes;
        if (_prevScheme != null) { try { PowerPlan.Activate(_prevScheme); } catch (Exception ex) { Infrastructure.AppLog.Error("modo jogo", ex); } _prevScheme = null; }
        if (_prevOverlay != null) { try { PowerPlan.SetOverlay(_prevOverlay); } catch (Exception ex) { Infrastructure.AppLog.Error("modo jogo", ex); } _prevOverlay = null; }
        string text = $"{minutes:0} min de jogo";
        if (_samples >= 10 && minutes >= 1)
        {
            // Com a gravação completa: médias exatas (todos os frames), 0,1% low e engasgos. Sem ela, a média das amostras.
            var a = times.Count > 600 ? FrameAnalysis.Analyze(_gameName, _start, times, dropped, MemorySamplesFor(times)) : null;
            var rec = new GameSessionRecord(_start, _gameName, Math.Round(minutes, 1),
                                            Math.Round(a?.AvgFps ?? _fpsSum / _samples, 1), Math.Round(a?.Low1Fps ?? _lowSum / _samples, 1),
                                            _s.TurboAppliedAt is DateTime t && _start >= t,
                                            Math.Round(a?.Low01Fps ?? 0, 1), a?.Stutters ?? 0);
            _s.Mutate(() =>
            {
                _s.GameSessions.Insert(0, rec);
                if (_s.GameSessions.Count > 300) _s.GameSessions.RemoveRange(300, _s.GameSessions.Count - 300);
            });
            AfterSessionForTimer(rec);
            _s.Save();
            text += $" · {rec.AvgFps:0} FPS médio · 1% low {rec.Low1Fps:0}" + (rec.Low01Fps > 0 ? $" · 0,1% {rec.Low01Fps:0} · {rec.Stutters} engasgo(s)" : "");
            if (a?.Diagnosis is { Verdict: not StutterVerdict.None } diagnosis) text += " · " + diagnosis.Summary;
        }
        // Fora do caminho do resto: sem disco, a sessão só deixa de aparecer no Histórico.
        var finished = _session;
        _session = null;
        var saved = _s.Read(x => x.GameSessions.FirstOrDefault());
        Restore(() =>
        {
            bool mine = saved != null && saved.Start == _start;
            finished?.End(times, mine ? saved!.AvgFps : 0, mine ? saved!.Low1Fps : 0, mine ? saved!.Stutters : 0);
            finished?.Dispose();
        });
        if (_purges > 0) text += $" · memória em espera esvaziada {_purges}x";
        if (restrained > 0) text += $" · {restrained} programa(s) de fundo seguraram a CPU e foram rebaixados";
        _purges = 0;
        Notify?.Invoke($"Modo Jogo encerrado · {_gameName}", text + " · Windows voltou ao normal");
    }

    /// <summary>A sessão que acabou era a da medição do timer: conclui. Senão, se já há sessões para comparar, oferece medir.</summary>
    public event Action<string, string>? TimerMeasurementOffered;

    private string ProfileKey(string game) => _library.FirstOrDefault(g => g.Name == game)?.Key ?? game;

    private void AfterSessionForTimer(GameSessionRecord rec)
    {
        string key = ProfileKey(rec.Game);
        if (Core.Performance.TimerMeasurement.Conclude(_s, key, rec) is { } message)
        {
            Infrastructure.AppLog.Info("modo jogo", message);
            Notify?.Invoke($"Modo Jogo encerrado · timer em {rec.Game}", message);
            return;
        }
        bool offer = _s.Read(x => Core.Performance.TimerMeasurement.ShouldOffer(x.PendingTimerMeasurement, x.GameProfiles.GetValueOrDefault(key),
            x.GameSessions.Count(g => g.Game == rec.Game), x.GameModeTimer));
        if (offer) TimerMeasurementOffered?.Invoke(rec.Game, key);
    }

    private static void ResetSelf()
    {
        try { using var self = Process.GetCurrentProcess(); PowerThrottling.Reset(self); } catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
    }

    public void Dispose()
    {
        lock (_sync) DisposeCore();
    }

    private void DisposeCore()
    {
        _timer.Release(); // primeiro e sem depender do resto: nada abaixo pode segurar o pedido
        _session?.Dispose(); // o app fechando com o jogo aberto: sem arquivo pela metade
        _session = null;
        Restore(RestoreGameProcess); // fechar o tHg com o jogo aberto não pode deixar o disco em prioridade alta
        Restore(() => _clients.RestoreAll());
        Restore(() => _balancer.RestoreAll());
        Restore(() => _eco.RestoreAll());
        Restore(() => _cores.RestoreAll());
        Restore(() => Notifications.Restore(_s));
        Restore(() => ProcessPause.ResumeAll(_s));
        Restore(() => GameServices.Restart(_s));
        if (_prevScheme != null) { try { PowerPlan.Activate(_prevScheme); } catch (Exception ex) { Infrastructure.AppLog.Error("modo jogo", ex); } _prevScheme = null; }
        if (_prevOverlay != null) { try { PowerPlan.SetOverlay(_prevOverlay); } catch (Exception ex) { Infrastructure.AppLog.Error("modo jogo", ex); } _prevOverlay = null; }
    }

    private int? ForegroundPid()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out uint pid);
        return pid == 0 || pid == Environment.ProcessId ? null : (int)pid;
    }

    /// <summary>Nome do processo da frente, sem abrir o processo. Só consulta o kernel quando a janela da frente muda de processo.</summary>
    private string? ForegroundName(int pid)
    {
        if (pid == _fgPid) return _fgName;
        _fgName = Infrastructure.ProcessUtil.NameOf(pid);
        _fgPid = pid;
        return _fgName;
    }

    /// <summary>
    /// Objeto Process do jogo que acabou de ser identificado. GetProcessById abre o processo para conferir se existe, e o Valorant nunca é aberto:
    /// para ele, a lista do Windows, que não abre nada, com o custo dela só nesse caso.
    /// </summary>
    internal static Process? OpenGame(int pid, string name)
    {
        if (CompetitiveProfiles.IsVanguardProtected(name))
        {
            Process? found = null;
            foreach (var p in Process.GetProcessesByName(name))
                if (p.Id == pid) found = p; else p.Dispose();
            return found;
        }
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; } // fechou entre a identificação e aqui
    }

    // Fica guardado enquanto o mesmo processo está na frente; End limpa (o jogo que fechou não volta a ser detectado).
    private int _fgPid;
    private string? _fgName;

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
        if (Infrastructure.PowerApi.ActiveScheme() is string native) return native;
        var m = GuidRx.Match(SecurityAudit.Run("powercfg.exe", "/getactivescheme", 10_000).Output);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Planos existentes: GUID e nome (o nome vem traduzido).</summary>
    public static List<(string Guid, string Name)> List()
    {
        if (Infrastructure.PowerApi.Schemes() is { } native) return native;
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
        if (Infrastructure.PowerApi.ReadAc(guid, SubNone, Personality) is int personality) return personality == 1;
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
    public static bool SetBestPerformanceOverlay() => SetOverlay(BestPerformanceOverlay);

    public const string BestPerformanceOverlay = "ded574b5-45a0-4f42-8737-46345c09c238";
    /// <summary>Overlay "Equilibrado" (o padrão: nenhum overlay).</summary>
    private const string NoOverlay = "00000000-0000-0000-0000-000000000000";

    public static bool SetOverlay(string guid) => SecurityAudit.Run("powercfg.exe", $"/overlaysetactive {guid}", 10_000).Code == 0;

    /// <summary>Modern Standby lido uma vez só (não muda com o PC ligado).</summary>
    public static bool IsModernStandby => _modern ??= ModernStandby();
    private static bool? _modern;

    /// <summary>Modo de energia atual (o da tomada ou o da bateria, conforme o PC está agora).</summary>
    public static string ActiveOverlay()
    {
        bool battery = System.Windows.SystemParameters.PowerLineStatus == System.Windows.PowerLineStatus.Offline;
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
        return (k?.GetValue(battery ? "ActiveOverlayDcPowerScheme" : "ActiveOverlayAcPowerScheme") as string)?.ToLowerInvariant() is { Length: 36 } g ? g : NoOverlay;
    }
}

/// <summary>
/// Topologia da CPU para o Modo Jogo: núcleos que rendem mais em jogos. Ryzen X3D de dois CCDs: o CCD com o L3 maior
/// (3D V-Cache). Intel híbrido (12ª geração em diante): os núcleos P (classe de eficiência mais alta).
/// Em CPUs comuns não há o que escolher e devolve null.
/// </summary>
public static unsafe class CpuTopology
{
    private static readonly Lazy<bool> _hybrid = new(() => GameCoresMask(out string w) != null && w.StartsWith("só núcleos P"));
    /// <summary>CPU com núcleos P e E (Intel 12ª geração ou mais nova, Core Ultra).</summary>
    public static bool IsHybrid => _hybrid.Value;

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
    public static bool SetPreferredCores(System.Diagnostics.Process p, ulong mask) => SetPreferredCoresById(p.Id, mask);

    /// <summary>Tira os núcleos preferidos: o processo volta a poder rodar em qualquer núcleo. Afinidade não desfaz CPU Sets.</summary>
    public static bool ClearPreferredCoresById(int pid) =>
        Infrastructure.ProcessUtil.WithHandle(pid, Infrastructure.ProcessUtil.SetLimitedInformation, h => SetProcessDefaultCpuSets(h, null, 0));

    /// <summary>O processo tem núcleos preferidos definidos? Sem conseguir ler, responde false.</summary>
    public static bool HasPreferredCores(int pid)
    {
        bool present = false;
        // Sem buffer: com CPU Sets definidos, a chamada falha com 122 e informa quantos são; sem nenhum, dá certo com 0.
        Infrastructure.ProcessUtil.WithHandle(pid, Infrastructure.ProcessUtil.QueryLimited, h =>
        {
            bool ok = GetProcessDefaultCpuSets(h, null, 0, out uint needed);
            present = PreferredCoresPresent(ok, Marshal.GetLastWin32Error(), needed);
            return true;
        });
        return present;
    }

    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Qualquer falha além de "buffer insuficiente" (acesso negado, processo fechado) não diz nada sobre o processo: é "não".</summary>
    internal static bool PreferredCoresPresent(bool succeeded, int lastError, uint needed) =>
        (succeeded || lastError == ErrorInsufficientBuffer) && needed > 0;

    /// <summary>Igual a SetPreferredCores, pelo PID e com handle mínimo. Os ids vêm da topologia já lida: o Windows não é consultado a cada aplicação.</summary>
    public static bool SetPreferredCoresById(int pid, ulong mask)
    {
        uint[] ids = CpuSetIdsFor(Infrastructure.CpuTopologySnapshot.Current.Cpus, mask);
        if (ids.Length == 0) return false;
        try
        {
            // Handle mínimo, fechado em seguida: pode ser o jogo (ver ProcessUtil.WithHandle).
            return Infrastructure.ProcessUtil.WithHandle(pid, Infrastructure.ProcessUtil.SetLimitedInformation, h =>
            {
                fixed (uint* a = ids) return SetProcessDefaultCpuSets(h, a, (uint)ids.Length);
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Infrastructure.AppLog.Debug("modo jogo", ex);
            return false;
        }
    }

    /// <summary>Ids de CPU Set dos processadores lógicos do grupo 0 que estão na máscara.</summary>
    internal static uint[] CpuSetIdsFor(IReadOnlyList<Infrastructure.LogicalCpu> cpus, ulong mask)
    {
        var ids = new List<uint>(cpus.Count);
        foreach (var c in cpus)
            if (c.Group == 0 && c.Index < 64 && (mask & (1UL << c.Index)) != 0) ids.Add(c.CpuSetId);
        return ids.ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(IntPtr process, uint* cpuSetIds, uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSets(IntPtr process, uint* cpuSetIds, uint count, out uint required);

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
    public static long Clean() => Clean(Folders());

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// As pastas ficam no perfil do usuário: um programa sem privilégio pode trocar uma subpasta por um atalho (junction) no meio da
    /// limpeza, e o tHg, como administrador, apagaria o que está do outro lado. Por isso a descida é manual, e cada subpasta é
    /// conferida na hora, logo antes de apagar os arquivos dela. Pasta recusada fica de fora, com tudo que há dentro.
    /// </summary>
    internal static long Clean(IEnumerable<string> folders)
    {
        long freed = 0;
        foreach (var root in folders)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                try
                {
                    dir.Refresh();
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || DduCleaner.PassesThroughReparse(dir.FullName)) continue;
                    foreach (var f in dir.EnumerateFiles("*", OneLevel))
                        try { long l = f.Length; f.Delete(); freed += l; } catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
                    foreach (var sub in dir.EnumerateDirectories("*", OneLevel)) pending.Push(sub);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    Infrastructure.AppLog.Debug("modo jogo", ex);
                }
            }
        }
        return freed;
    }
}

/// <summary>
/// O que é preciso para desfazer um ajuste em um programa de fundo. Só dados: um objeto Process guardado seguraria um handle do
/// Windows durante a partida inteira. Sem handle, o PID pode ser reaproveitado: quem restaura confere o nome antes de mexer.
/// </summary>
internal readonly record struct AdjustedProcess(int Pid, string Name, IntPtr PreviousAffinity, bool PriorityChanged);

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
        // Valorant/Vanguard nunca são abertos; os clientes do LoL/Riot ficam com o ClientDemoter (perfis automáticos).
        "VALORANT", "VALORANT-Win64-Shipping", "vgm", "LeagueClientUx", "LeagueClientUxRender", "RiotClientUx", "RiotClientUxRender",
    };

    private readonly Dictionary<int, (TimeSpan Cpu, int Strikes)> _last = new();
    private readonly Dictionary<int, AdjustedProcess> _restrained = new();
    private readonly HashSet<int> _seen = new();
    // Já avaliados e fora do alcance nesta sessão (acesso negado, jogo, outra prioridade): não são abertos de novo a cada 3 s.
    private readonly HashSet<int> _skip = new();
    private DateTime _lastSample;

    /// <summary>Como um processo é aberto depois de atingir o limite (os testes contam as aberturas).</summary>
    internal Func<int, System.Diagnostics.Process> Open { get; set; } = System.Diagnostics.Process.GetProcessById;

    /// <summary>Já passou o intervalo da próxima leitura? O Modo Jogo só pede a lista de processos se alguém for usar.</summary>
    internal bool Due => (DateTime.Now - _lastSample).TotalSeconds >= 3;

    public void Sample(List<Infrastructure.ProcessSnapshot> snapshot, int gamePid, Func<System.Diagnostics.Process, bool>? protect = null)
    {
        if (!Due) return;
        if (snapshot.Count == 0) return; // sem _lastSample novo: a próxima leitura ainda compara com o intervalo certo
        double secs = _lastSample == default ? 0 : (DateTime.Now - _lastSample).TotalSeconds;
        _lastSample = DateTime.Now;
        Evaluate(snapshot, gamePid, secs, protect);
    }

    /// <summary>
    /// Tempo de CPU vem da lista do kernel, sem abrir ninguém: ler Process.TotalProcessorTime em processos protegidos lançava
    /// dezenas de exceções a cada 3 s, em pleno jogo. Só quem passou do limite é aberto, e só uma vez por sessão.
    /// </summary>
    internal void Evaluate(List<Infrastructure.ProcessSnapshot> snapshot, int gamePid, double secs, Func<System.Diagnostics.Process, bool>? protect)
    {
        double capacity = Environment.ProcessorCount * secs * 1000; // ms de CPU disponíveis no intervalo
        _seen.Clear();
        foreach (var snap in snapshot)
        {
            _seen.Add(snap.Pid);
            if (snap.Pid == gamePid || snap.Pid <= 4 || Never.Contains(snap.Name) || CompetitiveProfiles.IsVanguardProtected(snap.Name)
                || _restrained.ContainsKey(snap.Pid) || _skip.Contains(snap.Pid)) continue;
            var cpu = TimeSpan.FromTicks(snap.TotalTime100ns);
            if (_last.TryGetValue(snap.Pid, out var prev) && capacity > 0)
            {
                double share = (cpu - prev.Cpu).TotalMilliseconds / capacity;
                int strikes = share > 0.10 ? prev.Strikes + 1 : 0;
                _last[snap.Pid] = (cpu, strikes);
                if (strikes >= 2) Restrain(snap, protect);
            }
            else _last[snap.Pid] = (cpu, 0);
        }
        // Remover durante a enumeração é permitido no Dictionary desde o .NET Core 3.0.
        foreach (int pid in _last.Keys) if (!_seen.Contains(pid)) _last.Remove(pid);
        _skip.IntersectWith(_seen); // PID que sumiu pode voltar como outro programa
    }

    private void Restrain(in Infrastructure.ProcessSnapshot snap, Func<System.Diagnostics.Process, bool>? protect)
    {
        System.Diagnostics.Process? p = null;
        try
        {
            // Sondar antes evita a Win32Exception das propriedades de Process quando o Windows nega o acesso.
            if (!Infrastructure.ProcessUtil.CanOpen(snap.Pid, Infrastructure.ProcessUtil.QueryInformation | Infrastructure.ProcessUtil.SetInformation)) { _skip.Add(snap.Pid); return; }
            p = Open(snap.Pid);
            if (p.PriorityClass != System.Diagnostics.ProcessPriorityClass.Normal || protect?.Invoke(p) == true
                || !Infrastructure.ProcessUtil.CanOpen(snap.Pid, Infrastructure.ProcessUtil.AllAccess)) // ProcessIo usa p.Handle
            { _skip.Add(snap.Pid); return; }
            p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
            ProcessIo.SetIoPriority(p, 1);      // disco: baixa
            ProcessIo.SetPagePriority(p, 2);    // memória: baixa (sai primeiro da RAM se faltar)
            _restrained[snap.Pid] = new AdjustedProcess(snap.Pid, p.ProcessName, IntPtr.Zero, PriorityChanged: true);
        }
        catch { _skip.Add(snap.Pid); }
        finally { p?.Dispose(); }
    }

    /// <summary>Devolve a prioridade normal a todos que foram rebaixados. Retorna quantos eram.</summary>
    public int RestoreAll()
    {
        int n = _restrained.Count;
        foreach (var adjusted in _restrained.Values)
        {
            try
            {
                using var p = Open(adjusted.Pid); // sumiu: ArgumentException, nada a restaurar
                if (!p.ProcessName.Equals(adjusted.Name, StringComparison.OrdinalIgnoreCase)) continue; // PID reaproveitado por outro programa
                if (p.PriorityClass == System.Diagnostics.ProcessPriorityClass.BelowNormal) p.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                ProcessIo.SetIoPriority(p, 2);
                ProcessIo.SetPagePriority(p, 5);
            }
            catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
        }
        _restrained.Clear(); _last.Clear(); _skip.Clear(); _lastSample = default;
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
        "upc", "UbisoftConnect", "GalaxyClient", "GalaxyClient Helper", "Discord", "Spotify", "msedgewebview2",
    };

    private readonly Dictionary<int, AdjustedProcess> _applied = new();
    // Avaliados e fora do alcance nesta sessão (acesso negado, pasta do jogo): não são abertos de novo a cada 10 s.
    private readonly HashSet<int> _skip = new();
    private DateTime _lastSample;

    /// <summary>Como um alvo é aberto (os testes contam as aberturas).</summary>
    internal Func<int, System.Diagnostics.Process> Open { get; set; } = System.Diagnostics.Process.GetProcessById;

    /// <summary>Já passou o intervalo da próxima leitura?</summary>
    internal bool Due => (DateTime.Now - _lastSample).TotalSeconds >= 10;

    /// <summary>Aplica o EcoQoS nos alvos ainda não marcados (a cada 10 s no máximo). Retorna quantos entraram agora.</summary>
    public int Sample(List<Infrastructure.ProcessSnapshot> snapshot, int gamePid, Func<System.Diagnostics.Process, bool>? protect = null, bool force = false)
    {
        if (!force && !Due) return 0;
        _lastSample = DateTime.Now;
        return Apply(snapshot, gamePid, protect);
    }

    /// <summary>Filtra pelo nome na lista do kernel: só os alvos ainda não marcados chegam a ser abertos.</summary>
    internal int Apply(List<Infrastructure.ProcessSnapshot> snapshot, int gamePid, Func<System.Diagnostics.Process, bool>? protect)
    {
        if (snapshot.Count == 0) return 0;
        if (_skip.Count > 0) _skip.IntersectWith(snapshot.Select(s => s.Pid)); // PID que sumiu pode voltar como outro programa
        int n = 0;
        foreach (var snap in snapshot)
        {
            if (snap.Pid == gamePid || _applied.ContainsKey(snap.Pid) || _skip.Contains(snap.Pid) || !Targets.Contains(snap.Name)
                || CompetitiveProfiles.IsVanguardProtected(snap.Name)) continue;
            System.Diagnostics.Process? p = null;
            try
            {
                if (!Infrastructure.ProcessUtil.CanOpen(snap.Pid, Infrastructure.ProcessUtil.QueryLimited)) { _skip.Add(snap.Pid); continue; }
                p = Open(snap.Pid);
                if (protect?.Invoke(p) == true || !PowerThrottling.SetEco(snap.Pid, true)) { _skip.Add(snap.Pid); continue; }
                _applied[snap.Pid] = new AdjustedProcess(snap.Pid, p.ProcessName, IntPtr.Zero, PriorityChanged: false);
                n++;
            }
            catch { _skip.Add(snap.Pid); }
            finally { p?.Dispose(); }
        }
        return n;
    }

    /// <summary>Devolve todos ao controle automático do Windows. Retorna quantos eram.</summary>
    public int RestoreAll()
    {
        int n = _applied.Count;
        foreach (var adjusted in _applied.Values)
        {
            try
            {
                using var p = Open(adjusted.Pid); // sumiu: ArgumentException, nada a restaurar
                if (p.ProcessName.Equals(adjusted.Name, StringComparison.OrdinalIgnoreCase)) PowerThrottling.Reset(adjusted.Pid); // senão, PID reaproveitado
            }
            catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
        }
        _applied.Clear(); _skip.Clear(); _lastSample = default;
        return n;
    }
}

/// <summary>
/// Programas de fundo (Discord, navegadores, Spotify, Steam/Epic) com preferência pelos threads de fundo do plano de CPU e prioridade
/// "abaixo do normal" enquanto o jogo roda. Sempre por CPU Sets: o Windows ainda usa os outros núcleos se aqueles saturarem. Só mexe em quem está com todos os núcleos e prioridade
/// normal (ou já rebaixado pelo ProBalance), e cada um volta à afinidade e à prioridade de antes quando o jogo fecha.
/// Os PIDs ficam salvos: se o tHg fechar no meio, a próxima abertura devolve todos os núcleos a eles.
/// </summary>
public sealed class BackgroundCores
{
    private static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "chrome", "msedge", "msedgewebview2", "steamwebhelper", "Spotify", "EpicGamesLauncher",
    };

    private readonly AppSettings _s;
    private readonly Dictionary<int, AdjustedProcess> _applied = new();
    // Avaliados e fora do alcance nesta sessão (acesso negado, pasta do jogo, afinidade do usuário): não são abertos de novo a cada 10 s.
    private readonly HashSet<int> _skip = new();
    private readonly HashSet<int> _alivePids = new();
    private DateTime _lastSample;

    /// <summary>Como um alvo é aberto (os testes contam as aberturas).</summary>
    internal Func<int, Process> Open { get; set; } = Process.GetProcessById;

    private Infrastructure.CpuSetPlan? _plan;

    public BackgroundCores(AppSettings s) => _s = s;

    /// <summary>Plano da sessão. Sem threads de fundo nele, nenhum programa é tocado.</summary>
    public void UsePlan(Infrastructure.CpuSetPlan plan) => _plan = plan;

    private ulong BackgroundMask => (_plan ?? Infrastructure.CpuSetPlanner.Plan(Infrastructure.CpuTopologySnapshot.Current, new(false, false, true))).BackgroundMask;

    /// <summary>Já passou o intervalo da próxima leitura?</summary>
    internal bool Due => (DateTime.Now - _lastSample).TotalSeconds >= 10;

    /// <summary>Prende os alvos ainda não presos (a cada 10 s no máximo). Retorna quantos entraram agora.</summary>
    public int Sample(List<Infrastructure.ProcessSnapshot> snapshot, int gamePid, Func<Process, bool>? protect = null, bool force = false)
    {
        LastAppliedCount = 0; // sem Apply nesta rodada, nada novo a gravar
        if (!force && !Due) return 0;
        _lastSample = DateTime.Now;
        ulong mask = BackgroundMask;
        if (mask == 0) return 0;
        return Apply(snapshot, gamePid, mask, Infrastructure.NativePerfApi.AllCoresMask(), protect);
    }

    /// <summary>Alvos presos na última rodada: o Modo Jogo grava os PIDs uma vez só, no fim do tick.</summary>
    public int LastAppliedCount { get; private set; }

    /// <summary>Filtra pelo nome na lista do kernel: só os alvos ainda não presos chegam a ser abertos.</summary>
    internal int Apply(List<Infrastructure.ProcessSnapshot> snapshot, int gamePid, ulong mask, ulong all, Func<Process, bool>? protect)
    {
        LastAppliedCount = 0;
        if (snapshot.Count == 0) return 0;
        if (_skip.Count > 0) // PID que sumiu pode voltar como outro programa
        {
            _alivePids.Clear();
            for (int i = 0; i < snapshot.Count; i++) _alivePids.Add(snapshot[i].Pid);
            _skip.IntersectWith(_alivePids);
        }
        int n = 0;
        foreach (var snap in snapshot)
        {
            if (snap.Pid == gamePid || _applied.ContainsKey(snap.Pid) || _skip.Contains(snap.Pid) || !Targets.Contains(snap.Name)
                || CompetitiveProfiles.IsVanguardProtected(snap.Name)) continue;
            Process? p = null;
            try
            {
                if (!Infrastructure.ProcessUtil.CanOpen(snap.Pid, Infrastructure.ProcessUtil.QueryInformation | Infrastructure.ProcessUtil.SetInformation)) { _skip.Add(snap.Pid); continue; }
                p = Open(snap.Pid);
                if (protect?.Invoke(p) == true) { _skip.Add(snap.Pid); continue; }
                var aff = p.ProcessorAffinity;
                var prio = p.PriorityClass;
                // Afinidade escolhida pelo usuário (ou outro programa) e prioridades fora do normal ficam como estão.
                if ((ulong)(long)aff != all || prio is not (ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal)) { _skip.Add(snap.Pid); continue; }
                // CPU Sets: fica fora do jogo, mas o Windows ainda pode usar outros núcleos se esses saturarem.
                // Recusado: fica como está. Afinidade rígida prenderia o programa num conjunto que pode não bastar.
                if (!CpuTopology.SetPreferredCoresById(snap.Pid, mask)) { _skip.Add(snap.Pid); continue; }
                bool changed = prio == ProcessPriorityClass.Normal;
                if (changed) p.PriorityClass = ProcessPriorityClass.BelowNormal;
                _applied[snap.Pid] = new AdjustedProcess(snap.Pid, p.ProcessName, aff, changed);
                int pid = snap.Pid;
                _s.Mutate(() => _s.BgCorePids.Add(pid));
                n++;
                LastAppliedCount++;
            }
            catch { _skip.Add(snap.Pid); }
            finally { p?.Dispose(); }
        }
        return n;
    }

    /// <summary>Devolve a afinidade e a prioridade de antes. Retorna quantos eram.</summary>
    public int RestoreAll()
    {
        int n = _applied.Count;
        foreach (var adjusted in _applied.Values)
        {
            try
            {
                using var p = Open(adjusted.Pid); // sumiu: ArgumentException, nada a restaurar
                if (!p.ProcessName.Equals(adjusted.Name, StringComparison.OrdinalIgnoreCase)) continue; // PID reaproveitado por outro programa
                CpuTopology.ClearPreferredCoresById(adjusted.Pid);
                p.ProcessorAffinity = adjusted.PreviousAffinity;
                if (adjusted.PriorityChanged && p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
        }
        _applied.Clear(); _skip.Clear(); _lastSample = default;
        Recover(_s); // o que ficou de uma sessão interrompida
        return n;
    }

    /// <summary>Depois de um fechamento no meio do jogo: quem ainda está preso nos 2 threads volta a ter todos e prioridade normal.</summary>
    private static void Recover(AppSettings s)
    {
        var pids = s.CopyList(x => x.BgCorePids);
        if (pids.Count == 0) return;
        ulong? mask = Infrastructure.NativePerfApi.LastTwoCoresMask();
        foreach (int pid in pids)
            try
            {
                using var p = Process.GetProcessById(pid);
                // O PID pode ter sido reaproveitado: só mexe se ainda é um alvo preso por CPU Sets ou exatamente nos 2 threads.
                if (!Targets.Contains(p.ProcessName)) continue;
                bool sets = CpuTopology.HasPreferredCores(pid);
                bool pinned = mask != null && (ulong)(long)p.ProcessorAffinity == mask;
                if (!sets && !pinned) continue;
                if (sets) CpuTopology.ClearPreferredCoresById(pid);
                if (pinned) p.ProcessorAffinity = (IntPtr)(long)Infrastructure.NativePerfApi.AllCoresMask();
                if (p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
        s.Mutate(s.BgCorePids.Clear);
        s.Save();
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

    // Pelo PID, com handle mínimo e fechado na hora: é o caminho para o jogo e para os programas de fundo.
    public static bool SetEco(int pid, bool eco) => Set(pid, ExecutionSpeed, eco ? ExecutionSpeed : 0);
    public static bool Reset(int pid) => Set(pid, 0, 0);

    private static bool Set(System.Diagnostics.Process p, uint control, uint state)
    {
        try { return Apply(p.Handle, control, state); }
        catch { return false; }
    }

    private static bool Set(int pid, uint control, uint state) =>
        Infrastructure.ProcessUtil.WithHandle(pid, Infrastructure.ProcessUtil.SetInformation, h => Apply(h, control, state));

    private static bool Apply(IntPtr handle, uint control, uint state)
    {
        var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = control, StateMask = state };
        return SetProcessInformation(handle, 4 /* ProcessPowerThrottling */, ref st, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
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
        catch (Exception ex) { Infrastructure.AppLog.Warn("modo jogo", ex); }
        s.QuietRestore = null;
        s.Save();
    }

    /// <summary>Avisa o Windows que a configuração mudou (WM_SETTINGCHANGE), sem esperar quem não responde.</summary>
    private static void Broadcast()
    {
        try { SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "Policy", 0x0002, 1000, out _); } catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
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
        "Discord", "obs64", "obs32", "steam", "steamwebhelper", "RiotClientServices", "VALORANT", "VALORANT-Win64-Shipping", "vgm",
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
                if (NtSuspendProcess(p.Handle) == 0) { int pid = p.Id; s.Mutate(() => s.SuspendedPids.Add(pid)); n++; }
            }
            catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
            finally { p.Dispose(); }
        }
        if (n > 0) s.Save();
        return n;
    }

    public static void ResumeAll(AppSettings s)
    {
        var pids = s.CopyList(x => x.SuspendedPids);
        if (pids.Count == 0) return;
        foreach (int pid in pids)
            try { using var p = Process.GetProcessById(pid); NtResumeProcess(p.Handle); } catch (Exception ex) { Infrastructure.AppLog.Warn("modo jogo", ex); }
        s.Mutate(s.SuspendedPids.Clear);
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

    /// <summary>
    /// Pelo PID, com handle mínimo: é o caminho para o jogo. O nível alto (3) exige SeIncreaseBasePriorityPrivilege, que
    /// o administrador tem mas vem desligado; sem ele o Windows responde STATUS_PRIVILEGE_NOT_HELD e o retorno é false.
    /// </summary>
    public static bool SetIoPriority(int pid, int level)
    {
        int clamped = Math.Clamp(level, 0, 3);
        if (clamped == 3) EnableHighIoPrivilege();
        return Infrastructure.ProcessUtil.WithHandle(pid, Infrastructure.ProcessUtil.SetInformation, h =>
        {
            int v = clamped;
            return NtSetInformationProcess(h, 33 /* ProcessIoPriority */, ref v, 4) == 0;
        });
    }

    /// <summary>Prioridade de disco atual, ou null se o Windows negar.</summary>
    internal static int? GetIoPriority(int pid)
    {
        int level = -1;
        bool ok = Infrastructure.ProcessUtil.WithHandle(pid, Infrastructure.ProcessUtil.QueryLimited, h =>
            NtQueryInformationProcess(h, 33 /* ProcessIoPriority */, ref level, 4, out _) == 0);
        return ok ? level : null;
    }

    private static bool _privilegeTried;

    private static void EnableHighIoPrivilege()
    {
        if (_privilegeTried) return;
        _privilegeTried = true;
        try { Infrastructure.NativePerfApi.EnablePrivilege("SeIncreaseBasePriorityPrivilege"); } catch (Exception ex) { Infrastructure.AppLog.Debug("modo jogo", ex); }
    }

    [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int length);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref int info, int length, out int returned);
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
            if (Infrastructure.ServiceStatus.IsRunning(name) is bool running)
            {
                if (!running) continue;
            }
            else
            {
                var (code, output) = SecurityAudit.Run("sc.exe", $"query {name}", 10_000);
                if (code != 0 || !output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) && !output.Contains(" 4 ", StringComparison.Ordinal)) continue;
            }
            if (SecurityAudit.Run("sc.exe", $"stop {name}", 20_000).Code == 0) { s.Mutate(() => s.StoppedServices.Add(name)); n++; }
        }
        if (n > 0) s.Save();
        return n;
    }

    public static void Restart(AppSettings s)
    {
        var stopped = s.CopyList(x => x.StoppedServices);
        if (stopped.Count == 0) return;
        foreach (var name in stopped.Where(n => Names.Contains(n)))
            SecurityAudit.Run("sc.exe", $"start {name}", 20_000);
        s.Mutate(s.StoppedServices.Clear);
        s.Save();
    }
}
