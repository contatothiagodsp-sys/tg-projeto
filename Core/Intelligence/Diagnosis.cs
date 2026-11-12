namespace ThgOptimization.Core.Intelligence;

/// <summary>As classes de problema que o motor sabe reconhecer. Inconclusive é uma resposta válida, não uma falha.</summary>
public enum DiagnosisKind
{
    GpuBound, CpuBound, MainThreadBound, MemoryPressure, VramPressure, ThermalThrottling, PowerLimit,
    CpuTemperature, GpuTemperature, Storage, ConcurrentProcesses, Driver, DpcIsr, FramePacing, Network, Stability, Inconclusive,
}

public enum ConfidenceLevel { Insufficient, Low, Medium, High }

/// <summary>Info = comportamento normal que vale explicar (GPU no limite em jogo pesado). Problem = prejudica a experiência.</summary>
public enum Severity { Info, Attention, Problem }

/// <summary>Confiança de um diagnóstico: uma nota de 0 a 1 e os motivos que a formaram, para a tela poder explicar.</summary>
public sealed record Confidence(double Score, IReadOnlyList<string> Reasons)
{
    public const double HighAt = 0.75, MediumAt = 0.5, LowAt = 0.3;

    public ConfidenceLevel Level => Score >= HighAt ? ConfidenceLevel.High : Score >= MediumAt ? ConfidenceLevel.Medium
                                  : Score >= LowAt ? ConfidenceLevel.Low : ConfidenceLevel.Insufficient;

    public string Text => Level switch
    {
        ConfidenceLevel.High => "alta",
        ConfidenceLevel.Medium => "média",
        ConfidenceLevel.Low => "baixa",
        _ => "insuficiente",
    };

    /// <summary>
    /// força × qualidade dos dados. A força vem da regra (o quanto a condição apareceu); a qualidade, da duração da coleta e da
    /// cobertura das grandezas de que a regra depende. cap limita a nota quando a medida é indireta (ex.: sem GPU Busy por frame).
    /// </summary>
    public static Confidence Build(double strength, TelemetryQuality q, IReadOnlyList<Metric> needs, double cap = 1, bool timeMatters = true, IEnumerable<string>? notes = null)
    {
        var reasons = new List<string>();
        double coverage = needs.Count == 0 ? 1 : needs.Min(q.CoverageOf);
        double time = timeMatters ? 0.3 + 0.7 * q.DurationFactor : 1;
        if (timeMatters) reasons.Add($"{q.Seconds / 60:0.#} min de coleta" + (q.DurationFactor < 1 ? " (coletas de 10 min ou mais dão mais certeza)" : ""));
        if (needs.Count > 0) reasons.Add($"cobertura das medidas usadas: {coverage * 100:0}%");
        if (cap < 1) reasons.Add("medida indireta: a confiança tem teto");
        if (notes != null) reasons.AddRange(notes);
        return new Confidence(Math.Clamp(Math.Clamp(strength, 0, 1) * time * coverage, 0, cap), reasons);
    }
}

/// <summary>Um dado que sustenta o diagnóstico: o que foi medido, quanto deu e o que isso quer dizer.</summary>
public sealed record Evidence(string Metric, string Value, string Meaning, MetricOrigin Origin = MetricOrigin.Captured);

public sealed record Finding(DiagnosisKind Kind, Severity Severity, string Title, string Interpretation, Confidence Confidence,
                             IReadOnlyList<Evidence> Evidence, IReadOnlyList<string> Limitations)
{
    public bool IsProblem => Severity != Severity.Info;
}

/// <summary>Resumo dos frames da sessão. Low1/Low01 ficam null quando a amostra não sustenta a conta.</summary>
public sealed record FrameSummary(int Frames, double AvgFps, double P50Ms, double P95Ms, double P99Ms, double? Low1Fps, double? Low01Fps,
                                  int Stutters, double StuttersPerMinute, int[] Histogram, MetricOrigin Origin);

public sealed record DiagnosisReport(SessionContext Context, TelemetryQuality Quality, IReadOnlyList<Finding> Findings,
                                     IReadOnlyList<string> RuledOut, IReadOnlyList<string> Limitations, FrameSummary? Frames)
{
    /// <summary>Os problemas, do mais sério e mais certo para o menos.</summary>
    public IEnumerable<Finding> Problems => Findings.Where(f => f.IsProblem && f.Kind != DiagnosisKind.Inconclusive)
                                                    .OrderByDescending(f => f.Severity).ThenByDescending(f => f.Confidence.Score);

    public Finding? Top => Problems.FirstOrDefault() ?? Findings.FirstOrDefault(f => f.Kind == DiagnosisKind.Inconclusive) ?? Findings.FirstOrDefault();

    public string Headline => Top switch
    {
        null => Quality.EnoughTime ? "Nenhum problema identificado nesta sessão." : "Coleta curta demais para um diagnóstico.",
        { Kind: DiagnosisKind.Inconclusive } t => t.Title,
        { IsProblem: false } t => t.Title + " (comportamento normal)",
        var t => $"{t.Title} · confiança {t.Confidence.Text}",
    };
}
