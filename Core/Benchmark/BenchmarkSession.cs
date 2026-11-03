namespace ThgOptimization.Core.Benchmark;

/// <summary>Uma captura do Performance Lab. Numa comparação de várias capturas, A e B são a mediana de cada métrica.</summary>
public sealed record BenchmarkSession(
    string Game, string ConfigLabel, DateTime CapturedAt,
    double AvgFps, double Low1Fps, double Low01Fps, double P99Ms,
    double StutterPct, double MedianFrameTimeMs,
    int Frames, double DurationSeconds, string? CsvPath)
{
    public double P999Ms { get; init; }
    /// <summary>Teste a que a captura pertence: repetições só se somam dentro do mesmo teste.</summary>
    public string ExperimentId { get; init; } = "";
    /// <summary>"A" ou "B". O rótulo é texto livre, e não serve para separar as configurações.</summary>
    public string Slot { get; init; } = "";

    public static BenchmarkSession From(CaptureResult r, string label, string experimentId, string slot) =>
        new(r.Game, label, r.Start, r.AvgFps, r.Low1Fps, r.Low01Fps, r.P99Ms,
            r.Frames > 0 ? 100.0 * r.Stutters / r.Frames : 0, r.Statistics?.P50Ms ?? 0,
            r.Frames, r.Seconds, r.CsvPath)
        {
            P999Ms = r.Statistics?.P999Ms ?? 0,
            ExperimentId = experimentId,
            Slot = slot,
        };
}

public enum AbTestMethod { None, StudentT, MannWhitney }

public enum AbVerdictKind { Inconclusive, Trivial, Noise, SmallImprovement, Improvement, Regression }

/// <summary>Resultado do A/B. Os deltas são da configuração testada sobre a de referência: delta positivo em stutter é piora.</summary>
public sealed record AbComparison(
    BenchmarkSession A, BenchmarkSession B,
    double AvgFpsDeltaPct, double Low1DeltaPct, double Low01DeltaPct,
    double StutterDeltaPct, bool IsStatisticallySignificant,
    double EffectSizeCohensD, string Verdict)
{
    public AbVerdictKind Kind { get; init; }
    public AbTestMethod Method { get; init; }
    /// <summary>Null quando nenhum teste é possível (uma captura de cada lado).</summary>
    public double? PValue { get; init; }
    public int CapturesA { get; init; } = 1;
    public int CapturesB { get; init; } = 1;
    public bool BaselineIsA { get; init; } = true;
    public string ExperimentId { get; init; } = "";
    public DateTime ComparedAt { get; init; }
    public string? ReportPath { get; init; }
}
