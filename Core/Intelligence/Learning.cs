using System.Globalization;

namespace ThgOptimization.Core.Intelligence;

/// <summary>O que fica de uma sessão para o aprendizado: poucos números, nenhum dado pessoal. Null = não medido naquela sessão.</summary>
public sealed record SessionSummary(DateTime Start, string Signature, double Minutes)
{
    public double? AvgFps { get; init; }
    public double? Low1Fps { get; init; }
    public double? P99Ms { get; init; }
    public double? StuttersPerMinute { get; init; }
    public double? GpuLoadMedian { get; init; }
    public double? CpuLoadMedian { get; init; }
    public double? CoreLoadMedian { get; init; }
    public double? CpuTempP95 { get; init; }
    public double? GpuTempP95 { get; init; }
    public double? CpuClockMedian { get; init; }
    public double? GpuClockMedian { get; init; }
    public double? RamMedian { get; init; }
    public double? VramPeak { get; init; }
    /// <summary>Diagnóstico principal da sessão (nome do DiagnosisKind) e a confiança dele.</summary>
    public string? TopFinding { get; init; }
    public double TopConfidence { get; init; }
    public string? Headline { get; init; }
    public string? GpuDriver { get; init; }
    public string? Resolution { get; init; }
    /// <summary>Ficou fora da referência por destoar das outras sessões.</summary>
    public bool Outlier { get; init; }

    public static SessionSummary From(SessionTelemetry s, DiagnosisReport report)
    {
        double? Median(Metric m) => report.Quality.Has(m) ? RobustStats.Median(s.Points.Select(p => p.Get(m)).Where(v => v != null).Select(v => v!.Value)) : null;
        double? P95(Metric m) => report.Quality.Has(m) ? RobustStats.Percentile(s.Points.Select(p => p.Get(m)).Where(v => v != null).Select(v => v!.Value), 0.95) : null;
        var fr = report.Frames;
        bool captured = fr is { Origin: MetricOrigin.Captured };
        return new SessionSummary(s.Context.Start, s.Context.Signature, Math.Round(s.DurationSeconds / 60, 1))
        {
            AvgFps = fr?.AvgFps,
            Low1Fps = fr?.Low1Fps,
            P99Ms = captured ? fr!.P99Ms : null,
            StuttersPerMinute = captured ? fr!.StuttersPerMinute : null,
            GpuLoadMedian = Median(Metric.GpuLoad),
            CpuLoadMedian = Median(Metric.CpuLoad),
            CoreLoadMedian = Median(Metric.CpuMaxCoreLoad),
            CpuTempP95 = P95(Metric.CpuTemp),
            GpuTempP95 = P95(Metric.GpuTemp),
            CpuClockMedian = Median(Metric.CpuClock),
            GpuClockMedian = Median(Metric.GpuClock),
            RamMedian = Median(Metric.Ram),
            VramPeak = report.Quality.Has(Metric.Vram) ? s.Points.Max(p => p.VramPct ?? 0) : null,
            TopFinding = report.Top?.Kind.ToString(),
            TopConfidence = report.Top?.Confidence.Score ?? 0,
            Headline = report.Headline,
            GpuDriver = s.Context.GpuDriver,
            Resolution = s.Context.Resolution,
        };
    }
}

/// <summary>Referência de um jogo numa condição (hardware + driver + resolução). Guarda as sessões aceitas; centro e dispersão saem delas.</summary>
public sealed class GameBaseline
{
    public const int MaxSessions = 20, MinEstablished = 3;

    public string Signature { get; set; } = "";
    public List<SessionSummary> Sessions { get; set; } = new();
    /// <summary>Sessões recentes que destoaram. Três seguidas na mesma direção viram a nova referência (mudança de regime).</summary>
    public List<SessionSummary> Held { get; set; } = new();
    public DateTime? RebuiltAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Established => Sessions.Count >= MinEstablished;

    public (double Center, double Spread)? Stat(Func<SessionSummary, double?> metric)
    {
        var v = Sessions.Select(metric).Where(x => x != null).Select(x => x!.Value).ToList();
        return v.Count < MinEstablished ? null : (RobustStats.Median(v), RobustStats.Mad(v));
    }
}

public enum LearningOutcomeKind
{
    /// <summary>Entrou na referência.</summary>
    Learned,
    /// <summary>Curta demais para representar o jogo: não ensina nada.</summary>
    TooShort,
    /// <summary>Primeira sessão nestas condições (driver, resolução ou hardware novos): começa uma referência separada.</summary>
    NewContext,
    /// <summary>Destoou do habitual: fica de fora da referência, aguardando confirmação.</summary>
    OutlierHeld,
    /// <summary>O comportamento mudou de forma consistente (patch, configuração): a referência foi reconstruída.</summary>
    RegimeChange,
}

/// <summary>Um desvio da sessão em relação ao habitual deste jogo neste PC.</summary>
public sealed record Deviation(string Metric, double Value, double Usual, double Spread, double Z, bool Worse)
{
    public double DeltaPct => Usual == 0 ? 0 : (Value - Usual) / Usual * 100;
}

public sealed record LearningOutcome(LearningOutcomeKind Kind, string Message, IReadOnlyList<Deviation> Deviations, int BaselineSessions)
{
    public bool HasRegression => Deviations.Any(d => d.Worse);
}

/// <summary>
/// Aprendizado incremental por jogo. A referência é a mediana das sessões aceitas na mesma condição; uma sessão anormal não entra
/// (fica retida), e só uma mudança confirmada por sessões seguidas troca a referência. Nada é comparado entre condições diferentes.
/// </summary>
public sealed class LearningEngine
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Sessão mais curta que isto não representa o jogo.</summary>
    public const double MinMinutes = 5;
    /// <summary>Desvio robusto a partir do qual a sessão é anormal.</summary>
    public const double OutlierZ = 3.5;
    /// <summary>Sessões anormais seguidas, na mesma direção, que confirmam a mudança de regime.</summary>
    public const int RegimeSessions = 3;

    private sealed record Tracked(string Name, Func<SessionSummary, double?> Get, bool HigherIsBetter, bool Core);

    private static readonly Tracked[] Metrics =
    {
        new("FPS médio", s => s.AvgFps, true, true),
        new("1% low", s => s.Low1Fps, true, true),
        new("Frametime P99", s => s.P99Ms, false, true),
        new("Engasgos por minuto", s => s.StuttersPerMinute, false, false),
        new("Temperatura da CPU (P95)", s => s.CpuTempP95, false, false),
        new("Temperatura da GPU (P95)", s => s.GpuTempP95, false, false),
        new("Clock da CPU", s => s.CpuClockMedian, true, false),
        new("Clock da GPU", s => s.GpuClockMedian, true, false),
    };

    /// <summary>Desvios da sessão contra a referência. Vazio = dentro do habitual, ou sem referência estabelecida.</summary>
    public static IReadOnlyList<Deviation> Compare(GameBaseline baseline, SessionSummary s)
    {
        var list = new List<Deviation>();
        if (!baseline.Established) return list;
        foreach (var m in Metrics)
        {
            if (m.Get(s) is not double value || baseline.Stat(m.Get) is not (double center, double spread)) continue;
            // Piso relativo de 3%: uma referência muito estável não transforma 1% de diferença em anomalia.
            double z = RobustStats.RobustZ(value, center, spread, relativeFloor: 0.03, absoluteFloor: 0.5);
            if (Math.Abs(z) < OutlierZ) continue;
            list.Add(new Deviation(m.Name, value, center, spread, z, Worse: m.HigherIsBetter ? z < 0 : z > 0));
        }
        return list;
    }

    public LearningOutcome Learn(List<GameBaseline> baselines, SessionSummary s)
    {
        if (s.Minutes < MinMinutes)
            return new LearningOutcome(LearningOutcomeKind.TooShort, $"Sessão de {s.Minutes:0.#} min: curta demais para entrar na referência (mínimo {MinMinutes:0} min).", Array.Empty<Deviation>(), 0);

        var b = baselines.FirstOrDefault(x => x.Signature == s.Signature);
        if (b == null)
        {
            b = new GameBaseline { Signature = s.Signature };
            baselines.Add(b);
            b.Sessions.Add(s);
            string why = baselines.Count > 1 ? "As condições mudaram (hardware, driver, resolução ou alimentação): começa uma referência nova, sem misturar com a anterior."
                                             : "Primeira sessão registrada: a referência deste jogo começa aqui.";
            return new LearningOutcome(LearningOutcomeKind.NewContext, why, Array.Empty<Deviation>(), 1);
        }

        var deviations = Compare(b, s);
        bool coreOutlier = deviations.Any(d => Metrics.First(m => m.Name == d.Metric).Core);
        if (!coreOutlier)
        {
            b.Held.Clear(); // voltou ao habitual: as retidas eram exceções
            b.Sessions.Add(s);
            if (b.Sessions.Count > GameBaseline.MaxSessions) b.Sessions.RemoveRange(0, b.Sessions.Count - GameBaseline.MaxSessions);
            string msg = b.Established ? $"Dentro do habitual. Referência com {b.Sessions.Count} sessões comparáveis."
                                       : $"Referência em formação: {b.Sessions.Count} de {GameBaseline.MinEstablished} sessões comparáveis.";
            return new LearningOutcome(LearningOutcomeKind.Learned, msg, deviations, b.Sessions.Count);
        }

        bool worse = deviations.Where(d => Metrics.First(m => m.Name == d.Metric).Core).Count(d => d.Worse) * 2 >= deviations.Count(d => Metrics.First(m => m.Name == d.Metric).Core);
        // Direção diferente das retidas anteriores: não é a mesma mudança, recomeça a contagem.
        if (b.Held.Count > 0 && _heldWorse.TryGetValue(b, out bool previous) && previous != worse) b.Held.Clear();
        _heldWorse[b] = worse;
        b.Held.Add(s with { Outlier = true });

        if (b.Held.Count >= RegimeSessions)
        {
            b.Sessions = b.Held.Select(h => h with { Outlier = false }).ToList();
            b.Held = new List<SessionSummary>();
            b.RebuiltAt = s.Start;
            _heldWorse.Remove(b);
            return new LearningOutcome(LearningOutcomeKind.RegimeChange,
                $"O comportamento mudou de forma consistente nas últimas {RegimeSessions} sessões (atualização do jogo ou configuração gráfica diferente). A referência foi reconstruída a partir delas.",
                deviations, b.Sessions.Count);
        }
        string detail = string.Join("; ", deviations.Take(3).Select(Describe));
        return new LearningOutcome(LearningOutcomeKind.OutlierHeld,
            $"Sessão fora do habitual ({detail}). Ela não altera a referência: se o comportamento se repetir em {RegimeSessions} sessões seguidas, a referência é refeita.",
            deviations, b.Sessions.Count);
    }

    // Direção da sequência retida de cada referência. Só em memória: ao reabrir o app, a sequência continua pelas sessões retidas gravadas.
    private readonly Dictionary<GameBaseline, bool> _heldWorse = new();

    public static string Describe(Deviation d) =>
        $"{d.Metric} {d.Value.ToString("0.#", Pt)} contra {d.Usual.ToString("0.#", Pt)} habitual ({d.DeltaPct.ToString("+0;-0", Pt)}%)";
}
