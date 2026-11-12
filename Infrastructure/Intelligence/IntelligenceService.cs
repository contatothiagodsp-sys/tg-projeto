using System.Diagnostics;
using System.IO;
using ThgOptimization.Core.Benchmark;
using ThgOptimization.Core.Intelligence;

namespace ThgOptimization.Infrastructure.Intelligence;

/// <summary>Converte a linha do tempo gravada pelo Modo Jogo na telemetria que o motor entende. Não lê sensor nenhum.</summary>
public static class SessionTelemetryAdapter
{
    public static SessionTelemetry From(SessionTimeline t, SessionContext context, ExternalEvidence? external = null)
    {
        bool frames = t.FramesMs.Count > 0;
        var points = new List<TelemetryPoint>(t.Samples.Count);
        foreach (var s in t.Samples)
            points.Add(new TelemetryPoint(s.TimestampMs / 1000.0)
            {
                Fps = s.Fps,
                // Sem gravação de frames, FrametimeMs é a média do segundo, não o pior frame: não entra como pico.
                WorstFrameMs = frames ? s.FrametimeMs : null,
                Stutters = s.StuttersThisSecond,
                CpuLoad = s.CpuLoad, CpuMaxCoreLoad = s.CpuMaxCoreLoad, CpuClockMhz = s.CpuClock, CpuTemp = s.CpuTemp, CpuPowerW = s.CpuPower,
                GpuLoad = s.GpuLoad, GpuClockMhz = s.GpuClock, GpuTemp = s.GpuTemp, GpuPowerW = s.GpuPower,
                VramPct = Finite(s.VramPct), RamPct = s.RamPct, CommitPct = s.CommitPct,
                NetLatencyMs = s.NetLatencyMs, NetLost = s.NetLost, HardwareError = s.WheaError,
            });
        return new SessionTelemetry(context, points, t.FramesMs, external);
    }

    private static double? Finite(double? v) => v is double d && double.IsFinite(d) ? d : null;
}

/// <summary>Monta o inventário do PC a partir do que o app já coletou (WMI, sensores, topologia). Não abre nenhum sensor novo.</summary>
public static class MachineProfileBuilder
{
    // Fora do inventário do Intelligence: identificam a pessoa ou a máquina, e o motor não precisa deles.
    private static readonly HashSet<string> Private = new(StringComparer.OrdinalIgnoreCase) { "Nº de série", "Nome do computador", "Part number" };

    public static MachineProfile Build(IEnumerable<InfoNode> system, IReadOnlyList<GpuInfo> gpus, CpuTopologySnapshot topology, SensorMonitor? monitor,
                                       GamerAudit.PowerState power, string cpuName, bool frameCapture, DateTime now)
    {
        var groups = new List<CapabilityGroup>();
        foreach (var node in system)
        {
            var items = new List<Capability>();
            Flatten(node, "", items, 0);
            if (items.Count > 0) groups.Add(new CapabilityGroup(node.Header, items));
        }

        string gpuName = gpus.Count > 0 ? gpus[0].Name : "";
        string? driver = gpus.SelectMany(g => g.Sections).Where(s => s.Header == "DRIVER E BIOS")
                             .SelectMany(s => s.Children).FirstOrDefault(n => n.Header == "Versão do driver")?.Value;
        var (resolution, hz) = Display(system);
        double? ramGb = Optimizer.MemoryStatus().Total is > 0 and var total ? total / 1073741824.0 : null;

        var sensors = new List<Capability>
        {
            Sensor("Uso da CPU", monitor?.CpuLoad, "%"),
            Sensor("Temperatura da CPU", monitor?.CpuTemperature, "°C", SensorMonitor.IsElevated ? "o sensor não respondeu" : "precisa de administrador"),
            Sensor("Consumo da CPU", monitor?.CpuPower, "W"),
            Sensor("Uso da GPU", monitor?.GpuCoreLoad, "%", gpus.Count == 0 ? "nenhuma placa de vídeo detectada" : "o driver não informa"),
            Sensor("Temperatura da GPU", monitor?.GpuTemperature, "°C", gpus.Count == 0 ? "nenhuma placa de vídeo detectada" : "o driver não informa"),
            frameCapture ? new Capability("Frametime por frame (ETW)", ReadingState.Supported, "disponível")
                         : new Capability("Frametime por frame (ETW)", ReadingState.Unavailable, "", "a sessão ETW de frames não está ativa"),
            Capability.Unsupported("GPU Busy e tempo de CPU por frame", "exigem o serviço do Intel PresentMon, que o tHg não instala"),
            Capability.Unsupported("Latência de entrada (input lag)", "exige hardware de medição; o tHg não estima"),
            Capability.Unsupported("Limites de potência do firmware (PL1/PL2/PPT)", "não são lidos"),
        };

        var vendors = gpus.Select(g => g.Vendor).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool integrated = gpus.Any(g => IsIntegrated(g.Name));
        return new MachineProfile(now, MachineProfile.MakeHardwareId(cpuName, gpuName, ramGb), groups)
        {
            CpuName = cpuName, GpuName = gpuName, GpuVendor = gpus.Count > 0 ? gpus[0].Vendor : "", GpuDriver = driver,
            LogicalProcessors = topology.LogicalCount, PhysicalCores = topology.PhysicalCount, HasSmt = topology.HasSmt,
            IsHybridCpu = topology.IsHybrid, HasAsymmetricCache = topology.HasAsymmetricCache, ProcessorGroups = topology.GroupCount,
            IsLaptop = power.HasBattery, OnBattery = power.OnBattery,
            HasIntegratedAndDedicatedGpu = gpus.Count >= 2 && integrated && (vendors >= 2 || gpus.Any(g => !IsIntegrated(g.Name))),
            RamGb = ramGb, Resolution = resolution, RefreshHz = hz, WindowsBuild = Environment.OSVersion.Version.Build.ToString(), Sensors = sensors,
        };
    }

    internal static bool IsIntegrated(string name) =>
        name.Contains("UHD", StringComparison.OrdinalIgnoreCase) || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)
        || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Radeon Graphics", StringComparison.OrdinalIgnoreCase);

    private static Capability Sensor(string name, float? value, string unit, string why = "sem leitura neste PC") =>
        value is float v ? new Capability(name, ReadingState.Supported, $"{v:0.#} {unit}") : new Capability(name, ReadingState.Unavailable, "", why);

    private static void Flatten(InfoNode node, string prefix, List<Capability> items, int depth)
    {
        if (depth > 3 || items.Count >= 80) return;
        foreach (var child in node.Children)
        {
            if (Private.Contains(child.Header)) continue;
            string name = prefix.Length > 0 ? $"{prefix} · {child.Header}" : child.Header;
            if (child.Children.Count > 0) Flatten(child, name, items, depth + 1);
            else items.Add(Capability.Of(name, child.Value));
        }
    }

    /// <summary>"1920x1080 @ 144 Hz" do nó de vídeo. Null quando o Windows não informou.</summary>
    internal static (string? Resolution, int? Hz) Display(IEnumerable<InfoNode> system)
    {
        foreach (var top in system)
            foreach (var gpu in top.Children)
                if (gpu.Children.FirstOrDefault(c => c.Header == "Resolução atual")?.Value is { Length: > 0 } text)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d{3,5})\s*x\s*(\d{3,5})\s*@\s*(\d{2,3})");
                    if (m.Success) return ($"{m.Groups[1].Value}x{m.Groups[2].Value}@{m.Groups[3].Value}", int.Parse(m.Groups[3].Value));
                }
        return (null, null);
    }
}

/// <summary>
/// O catálogo fechado de ações do tHg Intelligence. Cada ação liga ou desliga uma opção do Modo Jogo que o app já tem: elas valem só
/// enquanto um jogo roda e o próprio Modo Jogo devolve tudo ao fechar o jogo. O motor não tem como escrever em outro lugar.
/// </summary>
public static class ActionCatalog
{
    /// <summary>Alvo que é um campo booleano das configurações. Ler, escrever e confirmar passam pela trava das configurações.</summary>
    private sealed class SettingTarget(AppSettings s, Action<AppSettings> save, Func<AppSettings, bool> get, Action<AppSettings, bool> set, Func<string?>? incompatible = null) : IActionTarget
    {
        public string? Incompatibility(ActionScope scope) => incompatible?.Invoke();
        public string? Read(ActionScope scope) => s.Read(get) ? "on" : "off";
        public string Desired(ActionScope scope) => "on";

        public void Write(ActionScope scope, string state, CancellationToken ct)
        {
            if (state is not ("on" or "off")) throw new ArgumentException("Estado inválido para esta ação.", nameof(state));
            ct.ThrowIfCancellationRequested();
            s.Mutate(() => set(s, state == "on"));
            save(s);
        }
    }

    /// <param name="save">Como as configurações são gravadas depois de cada escrita (os testes passam uma gravação vazia).</param>
    public static IReadOnlyList<(ActionSpec Spec, IActionTarget Target)> Build(AppSettings s, Func<CpuTopologySnapshot>? topology = null, Action<AppSettings>? save = null)
    {
        topology ??= () => CpuTopologySnapshot.Current;
        save ??= x => x.Save();
        const string OnlyInGame = "Vale só enquanto um jogo está aberto. O Modo Jogo devolve tudo ao normal quando o jogo fecha.";
        return new (ActionSpec, IActionTarget)[]
        {
            (new ActionSpec(ActionIds.AutoGameMode, "Ligar o Modo Jogo automático",
                "O tHg passa a reconhecer os jogos da sua biblioteca, gravar a sessão e diagnosticar quando ela termina.",
                "Sem o Modo Jogo automático não há sessão para analisar.", RiskClass.B)
             { Changes = "Opção \"Modo Jogo automático\" do tHg.", Compatibility = "Qualquer PC.", MinConfidence = ConfidenceLevel.Low,
               HowToUndo = "Desligar em Jogos › Modo Jogo, ou desfazer no histórico do tHg Intelligence." },
             new SettingTarget(s, save, x => x.AutoGameMode, (x, v) => x.AutoGameMode = v)),

            (new ActionSpec(ActionIds.ProBalance, "Segurar programas de fundo que disputam a CPU",
                "Durante o jogo, um programa de fundo que passa de 10% da CPU por leituras seguidas vai para prioridade abaixo do normal.",
                "Menos disputa pela CPU durante a partida.", RiskClass.B)
             { Changes = "Opção \"Equilíbrio de processos\" do Modo Jogo. " + OnlyInGame, Compatibility = "Qualquer PC. Sistema, anti-cheat, áudio e gravação nunca são tocados.",
               Evidence = new[] { DiagnosisKind.ConcurrentProcesses } },
             new SettingTarget(s, save, x => x.GameModeProBalance, (x, v) => x.GameModeProBalance = v)),

            (new ActionSpec(ActionIds.EcoQos, "Modo de eficiência nos programas de fundo",
                "Durante o jogo, navegadores e launchers conhecidos rodam em modo de eficiência (EcoQoS). O jogo nunca recebe EcoQoS.",
                "Sobra CPU para o jogo sem fechar nada.", RiskClass.B)
             { Changes = "Opção \"EcoQoS nos programas de fundo\" do Modo Jogo. " + OnlyInGame, Compatibility = "Windows 11 ou Windows 10 2004 em diante.",
               Evidence = new[] { DiagnosisKind.ConcurrentProcesses } },
             new SettingTarget(s, save, x => x.GameModeEcoQos, (x, v) => x.GameModeEcoQos = v)),

            (new ActionSpec(ActionIds.PowerPlan, "Plano de alto desempenho durante o jogo",
                "O tHg ativa o plano de energia de alto desempenho ao abrir o jogo e devolve o plano anterior ao fechar.",
                "Evita que o plano de energia segure a frequência da CPU durante a partida.", RiskClass.B)
             { Changes = "Opção \"Plano de energia\" do Modo Jogo. " + OnlyInGame, Compatibility = "Em notebook na bateria gasta mais energia.",
               Evidence = new[] { DiagnosisKind.PowerLimit } },
             new SettingTarget(s, save, x => x.GameModePowerPlan, (x, v) => x.GameModePowerPlan = v)),

            (new ActionSpec(ActionIds.CpuSets, "Preferir os núcleos mais rápidos para o jogo (CPU Sets)",
                "Durante o jogo, o Windows passa a preferir os núcleos de desempenho (ou o CCD com 3D V-Cache) para o processo do jogo.",
                "Pode reduzir picos de frametime quando o jogo depende de uma thread principal.", RiskClass.C)
             { Changes = "Opção \"Núcleos preferidos\" do Modo Jogo (CPU Sets, não afinidade fixa). " + OnlyInGame,
               Compatibility = "Só em CPUs com núcleos diferentes entre si: Intel híbrido (P/E-cores) ou Ryzen com dois CCDs de cache diferente.",
               Evidence = new[] { DiagnosisKind.MainThreadBound, DiagnosisKind.CpuBound },
               HowToMeasure = "O tHg compara as 3 próximas sessões comparáveis deste jogo com as anteriores e desfaz sozinho se piorar ou não houver ganho." },
             new SettingTarget(s, save, x => x.GameModeAffinity, (x, v) => x.GameModeAffinity = v,
                 () => topology() is { IsHybrid: false, HasAsymmetricCache: false } ? "Este processador tem núcleos iguais entre si: não há núcleos \"mais rápidos\" para preferir." : null)),

            (new ActionSpec(ActionIds.BackgroundCores, "Separar os programas de fundo dos núcleos do jogo",
                "Durante o jogo, Discord, navegadores e launchers são direcionados por CPU Sets para os núcleos que o jogo usa menos.",
                "Deixa os núcleos mais rápidos livres para o jogo.", RiskClass.B)
             { Changes = "Opção \"Núcleos para programas de fundo\" do Modo Jogo. " + OnlyInGame, Compatibility = "CPUs com 12 threads ou mais.",
               Evidence = new[] { DiagnosisKind.MainThreadBound, DiagnosisKind.CpuBound } },
             new SettingTarget(s, save, x => x.GameModeBgCores, (x, v) => x.GameModeBgCores = v,
                 () => topology().LogicalCount < 12 ? "Com menos de 12 threads, separar os programas de fundo tira núcleos do próprio jogo." : null)),
        };
    }
}

/// <summary>
/// O tHg Intelligence dentro do app: guarda o store, o executor e o orquestrador, acompanha a sessão de jogo pelo controlador e roda
/// o diagnóstico fora da thread da janela quando o jogo fecha. A janela só observa e desenha.
/// </summary>
public sealed class IntelligenceService : IDisposable
{
    private readonly AppSettings _s;
    private readonly SelfImpactGovernor _self = new();
    private readonly object _gate = new();
    private Task _analysis = Task.CompletedTask;
    private TimeSpan _lastCpu;
    private long _lastCpuAt;
    private GameModeEngine? _gm;
    private bool _disposed;

    public IntelligenceStore Store { get; }
    public ActionExecutor Executor { get; }
    public IntelligenceOrchestrator Orchestrator { get; }
    public GameSessionController Controller { get; }
    /// <summary>Inventário do PC. A janela preenche depois que o hardware foi lido; até lá o motor trabalha sem ele.</summary>
    public MachineProfile? Machine { get; set; }
    public SessionAnalysis? LastAnalysis { get; private set; }
    /// <summary>Por que a última sessão não foi analisada, quando não foi.</summary>
    public string? LastSkipReason { get; private set; }

    /// <summary>A análise de uma sessão terminou. Chega fora da thread da janela.</summary>
    public event Action<SessionAnalysis>? Analyzed;
    /// <summary>O governador mudou o nível de coleta do próprio tHg (nível novo, CPU medida em %). Chega na thread que chamou SelfTick.</summary>
    public event Action<int, double>? SelfImpactChanged;

    public IntelligenceService(AppSettings settings) : this(settings, Path.Combine(AppSettings.Folder, "Intelligence", "intelligence.json")) { }

    internal IntelligenceService(AppSettings settings, string storePath, Func<CpuTopologySnapshot>? topology = null, Action<AppSettings>? save = null)
    {
        _s = settings;
        Store = new IntelligenceStore(storePath, w => AppLog.Warn("intelligence", w));
        Executor = new ActionExecutor(ActionCatalog.Build(settings, topology, save), Journal);
        Orchestrator = new IntelligenceOrchestrator(Store, Executor, () => Policy);
        Controller = new GameSessionController(new SessionHooks { Diagnose = OnGameEnded },
                                               log: (step, ex) => AppLog.Error("intelligence", $"Falha ao {step}: {ex.Message}"));
        try { Store.Prune(TimeSpan.FromDays(Math.Clamp(settings.IntelligenceRetentionDays, 7, 3650)), DateTime.Now); }
        catch (Exception ex) { AppLog.Warn("intelligence", ex); }
    }

    public AutomationLevel Level => _s.IntelligenceAutomation switch
    {
        "diagnosis" => AutomationLevel.DiagnosisOnly,
        "safe-auto" => AutomationLevel.SafeAuto,
        _ => AutomationLevel.Recommend,
    };

    public static string LevelKey(AutomationLevel level) => level switch
    {
        AutomationLevel.DiagnosisOnly => "diagnosis",
        AutomationLevel.SafeAuto => "safe-auto",
        _ => "recommend",
    };

    public AutomationPolicy Policy => new(Level, _s.Read(x => x.IntelligenceAutoActions.ToHashSet()));

    private void Journal(ActionRecord rec) => Store.Mutate(d =>
    {
        if (!d.Journal.Contains(rec)) d.Journal.Insert(0, rec);
    });

    /// <summary>Uma vez por segundo, da thread da janela: o que o Modo Jogo está vendo.</summary>
    public void Observe(GameModeEngine? gm, bool capturing)
    {
        _gm = gm;
        bool active = gm?.Active == true;
        Controller.Observe(new GameObservation(_s.AutoGameMode, active ? gm!.ActiveGamePid : null, active ? gm!.ActiveGameName : null,
                                               active ? gm!.ActiveGameKey : null, capturing));
    }

    /// <summary>O jogo fechou: pega o que o Modo Jogo gravou e analisa em segundo plano. Aqui só se agenda; nada pesado roda no tique.</summary>
    private void OnGameEnded(GameSessionInfo info)
    {
        var ended = _gm?.LastEnded;
        if (ended == null || ended.Game != info.GameName || ended.Timeline == null)
        {
            LastSkipReason = "A sessão foi curta demais para deixar uma linha do tempo.";
            return;
        }
        LastSkipReason = null;
        lock (_gate)
        {
            if (_disposed) return;
            _analysis = _analysis.ContinueWith(_ => AnalyzeEnded(ended), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void AnalyzeEnded(GameModeEngine.EndedSession ended)
    {
        try
        {
            var analysis = Orchestrator.Analyze(Telemetry(ended.Timeline!, ended.Key, ended.BackgroundOffenders, ended.InstallDir), Machine);
            LastAnalysis = analysis;
            Analyzed?.Invoke(analysis);
        }
        catch (Exception ex)
        {
            LastSkipReason = "A análise da sessão falhou: " + ex.Message;
            AppLog.Error("intelligence", ex);
        }
    }

    /// <summary>Monta a telemetria de uma linha do tempo com o contexto e as evidências externas que existem agora.</summary>
    public SessionTelemetry Telemetry(SessionTimeline timeline, string gameKey, IReadOnlyList<(string Name, int Strikes)>? offenders = null, string? installDir = null)
    {
        var m = Machine;
        var profile = Store.Game(gameKey, timeline.Header.Game);
        var context = new SessionContext(timeline.Header.Game, gameKey, timeline.Header.Start)
        {
            HardwareId = m?.HardwareId, GpuDriver = m?.GpuDriver, Resolution = m?.Resolution, OnBattery = m?.OnBattery, Preset = profile.Preset,
        };
        var latency = _s.LastLatency is { Error: null } l ? l : null;
        var external = new ExternalEvidence
        {
            DpcMaxMicroseconds = latency?.MaxUs, DpcMeasuredAt = latency?.When,
            DpcWorstDriver = latency?.Drivers.OrderByDescending(d => d.MaxUs).FirstOrDefault()?.File,
            GameOnHdd = OnHdd(installDir ?? profile.InstallDir),
            BackgroundHogs = (offenders ?? Array.Empty<(string, int)>()).Select(o => new BackgroundHog(o.Name, o.Strikes)).ToList(),
            RefreshHz = m?.RefreshHz,
        };
        if (installDir != null && profile.InstallDir != installDir) Store.Mutate(_ => profile.InstallDir = installDir);
        return SessionTelemetryAdapter.From(timeline, context, external);
    }

    /// <summary>O jogo está em HD mecânico? Null quando não dá para saber (caminho desconhecido, disco de rede, WMI indisponível).</summary>
    private static bool? OnHdd(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || dir.Length < 2 || dir[1] != ':') return null;
        try { return GamerAudit.PhysicalDiskOf(char.ToUpperInvariant(dir[0])) is var (media, bus, _) ? (media == 0 && bus != 17 ? null : media == 3) : null; }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            AppLog.Debug("intelligence", ex);
            return null;
        }
    }

    /// <summary>
    /// A cada 10 s: mede a CPU do próprio tHg e deixa o governador decidir. O efeito (perfil econômico, pausa de timers) é aplicado
    /// por quem assina SelfImpactChanged, porque são coisas da janela.
    /// </summary>
    public void SelfTick(bool gaming)
    {
        using var me = Process.GetCurrentProcess();
        var cpu = me.TotalProcessorTime;
        long now = Stopwatch.GetTimestamp();
        if (_lastCpuAt != 0)
        {
            double pct = ResourceProbe.CpuPercent(cpu - _lastCpu, Stopwatch.GetElapsedTime(_lastCpuAt, now), Environment.ProcessorCount);
            if (_self.Observe(pct, gaming) != SelfImpactDecision.Keep)
            {
                AppLog.Info("intelligence", $"Consumo do próprio tHg: {pct:0.0}% da CPU. Nível de coleta: {_self.Level}.");
                SelfImpactChanged?.Invoke(_self.Level, pct);
            }
        }
        _lastCpu = cpu;
        _lastCpuAt = now;
    }

    public int SelfImpactLevel => _self.Level;
    public double SelfCpuPercent => _self.LastCpuPct;

    public void Dispose()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _analysis;
        }
        Controller.Shutdown();
        try { pending.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { AppLog.Warn("intelligence", ex); }
    }
}
