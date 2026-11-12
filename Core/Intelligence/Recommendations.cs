namespace ThgOptimization.Core.Intelligence;

/// <summary>Perfil de uso de um jogo. É um ponto de partida para as recomendações, não um pacote de ajustes aplicado de uma vez.</summary>
public sealed record GamePreset(string Key, string Name, string Goal)
{
    public static readonly GamePreset Competitive = new("competitive", "Competitivo", "Consistência: frametime estável e nada disputando a CPU. Não troca estabilidade por FPS médio.");
    public static readonly GamePreset Performance = new("performance", "Performance", "Desempenho alto, mantendo segurança e estabilidade.");
    public static readonly GamePreset Balanced = new("balanced", "Equilibrado", "Boa relação entre fluidez, temperatura e consumo.");
    public static readonly GamePreset Quiet = new("quiet", "Silencioso", "Menos ruído e consumo, sem prejudicar a experiência sem necessidade.");
    public static readonly GamePreset Custom = new("custom", "Personalizado", "Você decide cada intervenção; o tHg só diagnostica e explica.");

    public static readonly IReadOnlyList<GamePreset> All = new[] { Competitive, Performance, Balanced, Quiet, Custom };

    public static GamePreset Parse(string? key) => All.FirstOrDefault(p => p.Key == key) ?? Balanced;

    /// <summary>O perfil aceita que esta ação seja sugerida? Silencioso não recebe o plano de alto desempenho; Personalizado, nenhuma ação.</summary>
    public bool Suggests(string actionId) => Key switch
    {
        "custom" => false,
        "quiet" => actionId != ActionIds.PowerPlan,
        _ => true,
    };
}

/// <summary>Ids do catálogo fechado de ações. O motor só conhece estes nomes; o que cada um altera está no alvo registrado pelo app.</summary>
public static class ActionIds
{
    public const string AutoGameMode = "gamemode.auto";
    public const string ProBalance = "gamemode.probalance";
    public const string EcoQos = "gamemode.ecoqos";
    public const string PowerPlan = "gamemode.powerplan";
    public const string CpuSets = "gamemode.cpusets";
    public const string BackgroundCores = "gamemode.bgcores";
}

/// <summary>Destinos que uma recomendação pode abrir. Lista fechada: nenhuma recomendação carrega caminho ou comando livre.</summary>
public static class Destinations
{
    public const string Fans = "page:PageFans", Overclock = "page:PageOc", Latency = "page:PageLatency", Network = "page:PageNet",
                        Startup = "page:PageStartup", Driver = "page:PageDriver", Lab = "page:PageBench", History = "page:PageHistory",
                        GameMode = "page:PageGameMode", Disks = "page:PageDisks", Energy = "page:PageEnergy",
                        WindowsGraphics = "uri:ms-settings:display-advancedgraphics";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    { Fans, Overclock, Latency, Network, Startup, Driver, Lab, History, GameMode, Disks, Energy, WindowsGraphics };

    public static string Label(string destination) => destination switch
    {
        Fans => "ABRIR FANS", Overclock => "VER ERROS DE HARDWARE", Latency => "MEDIR ENGASGOS (DPC)", Network => "ABRIR REDE",
        Startup => "VER INICIALIZAÇÃO", Driver => "ABRIR DRIVERS", Lab => "ABRIR PERFORMANCE LAB", History => "VER NA LINHA DO TEMPO",
        GameMode => "ABRIR MODO JOGO", Disks => "ABRIR DISCOS", Energy => "ABRIR ENERGIA", WindowsGraphics => "ABRIR GRÁFICOS DO WINDOWS",
        _ => "ABRIR",
    };
}

/// <summary>
/// Uma recomendação explicável: problema, evidências, proposta, benefício possível (sem promessa), risco, como desfazer e como medir.
/// ActionId = pode ser aplicada pelo executor. Destination = só orienta e abre a tela certa. As duas podem faltar: é só uma explicação.
/// </summary>
public sealed record Recommendation(string Id, DiagnosisKind Source, string Title, string Problem, string Proposal)
{
    public string Benefit { get; init; } = "";
    public RiskClass Risk { get; init; } = RiskClass.A;
    public string RiskText { get; init; } = "Nenhum: é uma orientação, o tHg não altera nada.";
    public string? ActionId { get; init; }
    public string? Destination { get; init; }
    public Confidence Confidence { get; init; } = new(0, Array.Empty<string>());
    public IReadOnlyList<Evidence> Evidence { get; init; } = Array.Empty<Evidence>();
    public bool NeedsRestart { get; init; }
    public string HowToUndo { get; init; } = "Nada a desfazer.";
    public string HowToMeasure { get; init; } = "";
    public string Compatibility { get; init; } = "";
    public int Priority { get; init; }
}

/// <summary>
/// Transforma diagnósticos em recomendações. Só propõe uma ação quando há um diagnóstico que a justifica, com a confiança mínima da
/// ação, quando ela é compatível com o PC e quando o usuário ainda não tem configuração equivalente. Sem problema, não recomenda nada.
/// </summary>
public sealed class RecommendationEngine
{
    private readonly ActionExecutor _executor;

    public RecommendationEngine(ActionExecutor executor) => _executor = executor;

    public IReadOnlyList<Recommendation> Recommend(DiagnosisReport report, MachineProfile? machine, GamePreset preset, ActionScope scope, ISet<string>? dismissed = null)
    {
        var list = new List<Recommendation>();
        foreach (var f in report.Findings)
        {
            if (f.Confidence.Level == ConfidenceLevel.Insufficient) continue;
            switch (f.Kind)
            {
                case DiagnosisKind.Stability:
                    list.Add(Advice(f, "stability", "Resolver a instabilidade antes de otimizar",
                        "Volte overclock, undervolt e perfil de memória (XMP/EXPO) para o padrão e teste de novo. Otimizar um PC instável só esconde o defeito.",
                        Destinations.Overclock, 100));
                    break;
                case DiagnosisKind.CpuTemperature or DiagnosisKind.GpuTemperature or DiagnosisKind.ThermalThrottling:
                    list.Add(Advice(f, "thermal", "Melhorar a refrigeração",
                        preset == GamePreset.Quiet
                            ? "Revise fluxo de ar e limpeza primeiro. Na curva das ventoinhas, suba a rotação só acima da temperatura em que a limitação começa, para manter o silêncio no resto."
                            : "Revise fluxo de ar, poeira e pasta térmica, e ajuste a curva das ventoinhas para reagir antes da faixa de limitação. Não alteramos clock nem tensão.",
                        Destinations.Fans, 90) with { Benefit = "Com a limitação térmica removida, o clock volta a se sustentar durante a partida. O ganho depende da causa." });
                    break;
                case DiagnosisKind.MemoryPressure:
                    list.Add(Advice(f, "ram", "Liberar memória antes de jogar",
                        "Feche navegadores e programas pesados e revise o que inicia com o Windows. Se a pressão for recorrente neste jogo, mais RAM é a solução proporcional. O tHg não limpa memória durante a partida.",
                        Destinations.Startup, 80));
                    break;
                case DiagnosisKind.VramPressure:
                    list.Add(Advice(f, "vram", "Reduzir o uso de memória de vídeo",
                        "Baixe a qualidade das texturas em um nível, ou a resolução de renderização. São as opções que mais ocupam VRAM.", null, 75));
                    break;
                case DiagnosisKind.ConcurrentProcesses:
                    AddAction(list, f, preset, scope, ActionIds.ProBalance, 70,
                        "Programas de fundo que passam de 10% da CPU por leituras seguidas vão para prioridade abaixo do normal durante o jogo e voltam ao normal quando ele fecha.");
                    AddAction(list, f, preset, scope, ActionIds.EcoQos, 60,
                        "Programas de fundo conhecidos (navegadores, launchers) rodam em modo de eficiência (EcoQoS) durante o jogo. O jogo nunca recebe EcoQoS.");
                    list.Add(Advice(f, "bg-close", "Decidir o que fica aberto durante o jogo",
                        "Veja a lista de programas que disputaram a CPU e feche os que não precisa. O tHg não encerra programas de terceiros.", Destinations.GameMode, 50));
                    break;
                case DiagnosisKind.MainThreadBound or DiagnosisKind.CpuBound:
                    if (machine is { IsHybridCpu: true } or { HasAsymmetricCache: true })
                        AddAction(list, f, preset, scope, ActionIds.CpuSets, 65,
                            machine!.IsHybridCpu
                                ? "Durante o jogo, o Windows passa a preferir os núcleos de desempenho (P-cores) para o jogo, por CPU Sets. É uma preferência, não uma trava: os outros núcleos continuam disponíveis."
                                : "Durante o jogo, o Windows passa a preferir o CCD com 3D V-Cache para o jogo, por CPU Sets. É uma preferência, não uma trava.");
                    if (machine != null && machine.LogicalProcessors >= 12)
                        AddAction(list, f, preset, scope, ActionIds.BackgroundCores, 55,
                            "Durante o jogo, programas de fundo são direcionados para os núcleos que o jogo usa menos, deixando os mais rápidos livres.");
                    list.Add(Advice(f, "cpu-settings", "Aliviar a CPU dentro do jogo",
                        "Reduza opções que pesam na CPU: distância de visão, densidade de multidões, física e sombras dinâmicas. Subir a resolução quase não custa FPS neste cenário.",
                        null, 45));
                    break;
                case DiagnosisKind.PowerLimit:
                    AddAction(list, f, preset, scope, ActionIds.PowerPlan, 60,
                        "Durante o jogo, o tHg ativa o plano de energia de alto desempenho e devolve o plano anterior quando o jogo fecha.");
                    list.Add(Advice(f, "power-bios", "Conferir os limites de potência", "Confira o plano de energia do Windows e, em notebook, se está na tomada. Limites da BIOS (PL1/PPT) não são alterados pelo tHg.",
                        Destinations.Energy, 40));
                    break;
                case DiagnosisKind.GpuBound:
                    if (machine is { HasIntegratedAndDedicatedGpu: true })
                        list.Add(Advice(f, "gpu-pref", "Conferir qual GPU o jogo usa",
                            "Este PC tem GPU integrada e dedicada. Em Configurações › Sistema › Tela › Elementos gráficos, confirme que o jogo está em \"Alto desempenho\". O tHg abre a tela; a escolha é sua.",
                            Destinations.WindowsGraphics, 55));
                    list.Add(Advice(f, "gpu-settings", "Mais FPS só com menos carga gráfica",
                        "A placa de vídeo já é bem aproveitada. Para subir o FPS: DLSS/FSR/XeSS, resolução de renderização ou um nível de qualidade abaixo. Nenhum ajuste do Windows muda este limite.",
                        null, 20));
                    break;
                case DiagnosisKind.FramePacing:
                    list.Add(Advice(f, "pacing-timeline", "Ver os momentos de engasgo na linha do tempo",
                        "O histórico da sessão mostra cada queda de fluidez e o que CPU, GPU, memória e temperatura faziam naquele segundo.", Destinations.History, 65));
                    if (preset == GamePreset.Competitive)
                        list.Add(Advice(f, "pacing-cap", "Testar um limite de FPS",
                            "Em jogos competitivos, travar o FPS um pouco abaixo do que o PC sustenta costuma estabilizar o frametime. Teste no Performance Lab (A/B) antes de adotar.",
                            Destinations.Lab, 35) with { Benefit = "Frametime mais regular. O efeito varia por jogo: meça antes de manter." });
                    break;
                case DiagnosisKind.DpcIsr:
                    list.Add(Advice(f, "dpc", "Refazer a medição de DPC/ISR", "O teste de engasgos mostra qual driver segura a CPU. Atualizar ou trocar esse driver é a correção.", Destinations.Latency, 60));
                    break;
                case DiagnosisKind.Storage:
                    list.Add(Advice(f, "storage", "Mover o jogo para um SSD", "Jogos atuais carregam dados durante a partida. Em HD mecânico isso vira travada.", Destinations.Disks, 55));
                    break;
                case DiagnosisKind.Network:
                    list.Add(Advice(f, "network", "Investigar a conexão",
                        "Prefira cabo ao Wi-Fi e confira downloads e streaming na rede. O teste de rede mede latência, perda e DNS. O tHg não altera DNS, rotas nem TCP/IP.", Destinations.Network, 50));
                    break;
                case DiagnosisKind.Driver:
                    list.Add(Advice(f, "driver", "Revisar o driver de vídeo", "A piora coincide com a troca de driver. Vale testar a versão anterior ou a mais recente.", Destinations.Driver, 60));
                    break;
            }
        }
        if (dismissed is { Count: > 0 }) list.RemoveAll(r => dismissed.Contains(r.Id));
        return list.GroupBy(r => r.Id).Select(g => g.OrderByDescending(r => r.Confidence.Score).First())
                   .OrderByDescending(r => r.Priority).ThenByDescending(r => r.Confidence.Score).ToList();
    }

    private static Recommendation Advice(Finding f, string id, string title, string proposal, string? destination, int priority) =>
        new(id, f.Kind, title, f.Title, proposal)
        {
            Destination = destination, Confidence = f.Confidence, Evidence = f.Evidence, Priority = priority,
            HowToMeasure = "Compare a próxima sessão deste jogo com a referência.",
        };

    private void AddAction(List<Recommendation> list, Finding f, GamePreset preset, ActionScope scope, string actionId, int priority, string proposal)
    {
        if (_executor.Spec(actionId) is not { } spec) return;                    // não registrada neste build
        if (!preset.Suggests(actionId)) return;                                   // o perfil do jogo não quer este tipo de ação
        if (f.Confidence.Level < spec.MinConfidence) return;                      // evidência abaixo do que a ação exige
        if (!spec.Evidence.Contains(f.Kind)) return;                              // a ação não trata este diagnóstico
        if (!_executor.Check(actionId, scope).Applicable) return;                 // incompatível, ou o usuário já tem equivalente
        list.Add(new Recommendation("action:" + actionId, f.Kind, spec.Name, f.Title, proposal)
        {
            ActionId = actionId, Risk = spec.Risk, Confidence = f.Confidence, Evidence = f.Evidence, Priority = priority,
            RiskText = spec.Risk == RiskClass.B ? "Baixo: ajuste reversível, vale só durante o jogo." : "Moderado: ajuste avançado. O efeito varia por jogo e precisa ser medido.",
            Benefit = spec.Goal + " Sem promessa de ganho: o resultado é medido nas próximas sessões.",
            NeedsRestart = spec.NeedsRestart, HowToUndo = spec.HowToUndo, HowToMeasure = spec.HowToMeasure, Compatibility = spec.Compatibility,
        });
    }
}
