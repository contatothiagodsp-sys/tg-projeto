using System.Globalization;

namespace ThgOptimization.Core.Intelligence;

public enum ExperimentVerdict
{
    /// <summary>Amostras insuficientes ou efeito indefinido: não dá para afirmar nada.</summary>
    Inconclusive,
    /// <summary>As condições das duas medições diferem: a comparação não é válida.</summary>
    Invalid,
    /// <summary>A diferença cabe na variação normal da medição.</summary>
    WithinNoise,
    Improvement,
    Regression,
}

/// <summary>O efeito em uma métrica: diferença relativa das medianas, intervalo de confiança e a leitura dele.</summary>
public sealed record MetricEffect(string Metric, bool HigherIsBetter, double Baseline, double Candidate, double DeltaPct,
                                  RobustStats.Interval? Interval, double NoisePct, ExperimentVerdict Verdict)
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public string Line
    {
        get
        {
            string ci = Interval is { } i ? $" (IC 95%: {i.Low.ToString("+0.#;-0.#;0", Pt)}% a {i.High.ToString("+0.#;-0.#;0", Pt)}%)" : "";
            string read = Verdict switch
            {
                ExperimentVerdict.Improvement => "melhora além da variação normal",
                ExperimentVerdict.Regression => "piora além da variação normal",
                ExperimentVerdict.WithinNoise => $"dentro da variação normal (±{NoisePct.ToString("0.#", Pt)}%)",
                _ => "inconclusivo",
            };
            return $"{Metric}: {Baseline.ToString("0.#", Pt)} → {Candidate.ToString("0.#", Pt)} ({DeltaPct.ToString("+0.#;-0.#;0", Pt)}%){ci} · {read}";
        }
    }
}

public sealed record ExperimentResult(ExperimentVerdict Verdict, string Summary, IReadOnlyList<MetricEffect> Effects, int BaselineRuns, int CandidateRuns)
{
    /// <summary>Manter a mudança só com melhora comprovada. Regressão ou ausência de benefício desfazem.</summary>
    public bool KeepChange => Verdict == ExperimentVerdict.Improvement;
    public bool ShouldRevert => Verdict is ExperimentVerdict.Regression or ExperimentVerdict.WithinNoise;
}

/// <summary>
/// Compara "antes" e "depois" de uma única mudança. Um ganho só conta se o intervalo de confiança (bootstrap das medianas) exclui o
/// zero e a diferença passa do dobro da variação normal da própria medição, com piso de 3%. Abaixo disso é ruído, não resultado.
/// </summary>
public static class ExperimentEvaluator
{
    /// <summary>Medições de cada lado para um veredito. Com menos, o resultado é sempre "inconclusivo".</summary>
    public const int MinRuns = 3;
    /// <summary>Piso da significância prática, em %: diferença menor que isto não é anunciada como ganho.</summary>
    public const double PracticalFloorPct = 3;

    private sealed record Tracked(string Name, Func<SessionSummary, double?> Get, bool HigherIsBetter, bool Primary);

    private static readonly Tracked[] Metrics =
    {
        new("1% low", s => s.Low1Fps, true, true),
        new("Frametime P99", s => s.P99Ms, false, true),
        new("Engasgos por minuto", s => s.StuttersPerMinute, false, true),
        new("FPS médio", s => s.AvgFps, true, false),
    };

    public static ExperimentResult Evaluate(IReadOnlyList<SessionSummary> baseline, IReadOnlyList<SessionSummary> candidate)
    {
        if (baseline.Count > 0 && candidate.Count > 0)
        {
            var signatures = baseline.Concat(candidate).Select(s => s.Signature).Distinct().ToList();
            if (signatures.Count > 1)
                return new ExperimentResult(ExperimentVerdict.Invalid,
                    "Comparação inválida: as sessões foram feitas em condições diferentes (hardware, driver, resolução ou alimentação).",
                    Array.Empty<MetricEffect>(), baseline.Count, candidate.Count);
        }
        if (baseline.Count < MinRuns || candidate.Count < MinRuns)
            return new ExperimentResult(ExperimentVerdict.Inconclusive,
                $"Ainda sem resultado: são necessárias {MinRuns} sessões comparáveis antes e {MinRuns} depois (há {baseline.Count} e {candidate.Count}).",
                Array.Empty<MetricEffect>(), baseline.Count, candidate.Count);

        var effects = new List<MetricEffect>();
        foreach (var m in Metrics)
        {
            var b = baseline.Select(m.Get).Where(v => v != null).Select(v => v!.Value).ToList();
            var c = candidate.Select(m.Get).Where(v => v != null).Select(v => v!.Value).ToList();
            if (b.Count < MinRuns || c.Count < MinRuns) continue; // métrica não medida nas sessões: fica de fora, sem valor inventado
            effects.Add(Effect(m.Name, m.HigherIsBetter, b, c));
        }
        if (effects.Count == 0)
            return new ExperimentResult(ExperimentVerdict.Inconclusive, "As sessões não têm as métricas necessárias (gravação de frames) para comparar.",
                                        effects, baseline.Count, candidate.Count);

        bool Primary(MetricEffect e) => Metrics.First(m => m.Name == e.Metric).Primary;
        var primary = effects.Where(Primary).ToList();
        var judged = primary.Count > 0 ? primary : effects;
        ExperimentVerdict verdict =
            effects.Any(e => e.Verdict == ExperimentVerdict.Regression) ? ExperimentVerdict.Regression
            : judged.Any(e => e.Verdict == ExperimentVerdict.Improvement) ? ExperimentVerdict.Improvement
            : judged.All(e => e.Verdict == ExperimentVerdict.WithinNoise) ? ExperimentVerdict.WithinNoise
            : ExperimentVerdict.Inconclusive;
        string summary = verdict switch
        {
            ExperimentVerdict.Improvement => "Melhora além da variação normal: " + string.Join("; ", effects.Where(e => e.Verdict == ExperimentVerdict.Improvement).Select(e => e.Metric)) + ".",
            ExperimentVerdict.Regression => "Regressão além da variação normal: " + string.Join("; ", effects.Where(e => e.Verdict == ExperimentVerdict.Regression).Select(e => e.Metric)) + ".",
            ExperimentVerdict.WithinNoise => "Nenhum benefício mensurável: as diferenças cabem na variação normal entre sessões.",
            _ => "Resultado inconclusivo: há diferença, mas o intervalo de confiança ainda inclui o zero. Mais sessões resolvem.",
        };
        return new ExperimentResult(verdict, summary, effects, baseline.Count, candidate.Count);
    }

    /// <summary>Efeito em uma métrica, a partir das medições repetidas de cada lado.</summary>
    public static MetricEffect Effect(string name, bool higherIsBetter, IReadOnlyList<double> baseline, IReadOnlyList<double> candidate)
    {
        double b = RobustStats.Median(baseline), c = RobustStats.Median(candidate);
        // Variação normal: a das repetições da referência. Com as do candidato junto, um efeito real inflaria o ruído.
        double noise = RobustStats.NoisePct(baseline.ToList());
        if (double.IsNaN(noise)) noise = PracticalFloorPct;
        double threshold = Math.Max(2 * noise, PracticalFloorPct);
        var interval = RobustStats.BootstrapMedianDeltaPct(baseline, candidate);
        // Referência zero (ex.: nenhum engasgo antes): a diferença relativa não existe; usa a diferença absoluta contra um piso.
        if (b == 0)
        {
            var zero = c == 0 ? ExperimentVerdict.WithinNoise : (c > 0) == higherIsBetter ? ExperimentVerdict.Improvement : ExperimentVerdict.Regression;
            if (Math.Abs(c) < 0.5) zero = ExperimentVerdict.WithinNoise;
            return new MetricEffect(name, higherIsBetter, b, c, 0, null, noise, zero);
        }
        double delta = (c - b) / b * 100;
        ExperimentVerdict verdict;
        if (interval is not { } ci) verdict = ExperimentVerdict.Inconclusive;
        else if (Math.Abs(delta) < threshold) verdict = ExperimentVerdict.WithinNoise;
        else if (!ci.ExcludesZero) verdict = ExperimentVerdict.Inconclusive;
        else verdict = (delta > 0) == higherIsBetter ? ExperimentVerdict.Improvement : ExperimentVerdict.Regression;
        return new MetricEffect(name, higherIsBetter, b, c, delta, interval, noise, verdict);
    }
}
