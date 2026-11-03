using System.Globalization;

namespace ThgOptimization.Core.Benchmark;

/// <summary>
/// Compara duas configurações, com uma medida por captura (FPS médio de 60 s). Os frames de uma captura não entram como amostras:
/// frames vizinhos são correlacionados, e com milhares deles qualquer diferença mínima pareceria significativa.
/// </summary>
public static class FrameTimeStatistics
{
    public const double Alpha = 0.05;
    private const int ExactRankLimit = 40;
    private const double MaxEffect = 99.9; // sem variância o d é infinito, e o JSON das configurações não aceita infinito

    public static AbComparison Compare(BenchmarkSession a, BenchmarkSession b, bool aIsBaseline) =>
        Compare(new[] { a }, new[] { b }, aIsBaseline);

    /// <summary>A e B do resultado são a mediana de cada métrica, para uma captura ruim não mover o resumo.</summary>
    public static AbComparison Compare(IReadOnlyList<BenchmarkSession> a, IReadOnlyList<BenchmarkSession> b, bool aIsBaseline)
    {
        if (a.Count == 0 || b.Count == 0) throw new ArgumentException("Cada configuração precisa de ao menos uma captura.");
        var medianA = Aggregate(a);
        var medianB = Aggregate(b);
        var (baseline, candidate) = aIsBaseline ? (medianA, medianB) : (medianB, medianA);
        var baselineFps = (aIsBaseline ? a : b).Select(s => s.AvgFps).ToArray();
        var candidateFps = (aIsBaseline ? b : a).Select(s => s.AvgFps).ToArray();

        var method = ChooseMethod(baselineFps.Length, candidateFps.Length);
        double? p = method switch
        {
            AbTestMethod.MannWhitney => MannWhitneyP(baselineFps, candidateFps),
            AbTestMethod.StudentT => StudentTP(baselineFps, candidateFps),
            _ => null,
        };
        bool significant = p < Alpha;
        double d = method == AbTestMethod.None ? 0 : CohensD(baselineFps, candidateFps);
        var (kind, verdict) = Classify(method, significant, d);

        return new AbComparison(medianA, medianB,
            DeltaPct(baseline.AvgFps, candidate.AvgFps), DeltaPct(baseline.Low1Fps, candidate.Low1Fps),
            DeltaPct(baseline.Low01Fps, candidate.Low01Fps), DeltaPct(baseline.StutterPct, candidate.StutterPct),
            significant, d, verdict)
        {
            Kind = kind,
            Method = method,
            PValue = p,
            CapturesA = a.Count,
            CapturesB = b.Count,
            BaselineIsA = aIsBaseline,
            ExperimentId = a[0].ExperimentId,
        };
    }

    /// <summary>
    /// Mann-Whitney a partir de 3 capturas de cada lado, se ele conseguir chegar a p &lt; 0,05. O menor p bilateral exato é
    /// 2 / C(n1+n2, n1): com 3 contra 3 ele é 0,10, e o teste nunca daria significativo. Nesses casos fica o t de Student.
    /// </summary>
    internal static AbTestMethod ChooseMethod(int n1, int n2)
    {
        if (n1 + n2 < 3) return AbTestMethod.None; // uma captura de cada lado não tem variância para estimar
        return n1 >= 3 && n2 >= 3 && 2 / Binomial(n1 + n2, n1) < Alpha ? AbTestMethod.MannWhitney : AbTestMethod.StudentT;
    }

    private static (AbVerdictKind, string) Classify(AbTestMethod method, bool significant, double d)
    {
        string D() => d.ToString("0.0", CultureInfo.InvariantCulture);
        if (method == AbTestMethod.None) return (AbVerdictKind.Inconclusive, "Inconclusivo: uma captura de cada lado não mede o ruído. Repita a captura.");
        if (Math.Abs(d) < 0.2) return (AbVerdictKind.Trivial, "Diferença trivial");
        if (!significant) return (AbVerdictKind.Noise, d > 0 ? "Melhora dentro do ruído" : "Piora dentro do ruído");
        if (d < 0) return (AbVerdictKind.Regression, $"Piora significativa (p < 0.05, d = {D()})");
        return d > 0.5
            ? (AbVerdictKind.Improvement, $"Melhora significativa (p < 0.05, d = {D()})")
            : (AbVerdictKind.SmallImprovement, $"Melhora pequena (p < 0.05, d = {D()})");
    }

    /// <summary>Mediana de cada métrica. O CSV é o da captura mais próxima da mediana de FPS: é ela que vai para o gráfico.</summary>
    private static BenchmarkSession Aggregate(IReadOnlyList<BenchmarkSession> s)
    {
        double fps = Median(s.Select(x => x.AvgFps));
        var typical = s.OrderBy(x => Math.Abs(x.AvgFps - fps)).First();
        var latest = s.OrderByDescending(x => x.CapturedAt).First();
        return new BenchmarkSession(latest.Game, latest.ConfigLabel, latest.CapturedAt, fps,
            Median(s.Select(x => x.Low1Fps)), Median(s.Select(x => x.Low01Fps)), Median(s.Select(x => x.P99Ms)),
            Median(s.Select(x => x.StutterPct)), Median(s.Select(x => x.MedianFrameTimeMs)),
            s.Sum(x => x.Frames), s.Sum(x => x.DurationSeconds), typical.CsvPath)
        {
            P999Ms = Median(s.Select(x => x.P999Ms)),
            ExperimentId = latest.ExperimentId,
            Slot = latest.Slot,
        };
    }

    internal static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return 0;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    internal static double DeltaPct(double baseline, double candidate) =>
        baseline != 0 ? (candidate - baseline) / baseline * 100 : 0;

    /// <summary>
    /// Cohen: d = (média2 - média1) / s, com s = raiz(((n1-1)·s1² + (n2-1)·s2²) / (n1+n2-2)).
    /// Positivo: a segunda amostra tem o maior valor.
    /// </summary>
    internal static double CohensD(IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        double diff = second.Average() - first.Average();
        double pooled = Math.Sqrt(PooledVariance(first, second));
        if (pooled == 0) return diff == 0 ? 0 : Math.CopySign(MaxEffect, diff);
        return Math.Clamp(diff / pooled, -MaxEffect, MaxEffect);
    }

    private static double PooledVariance(IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        int df = first.Count + second.Count - 2;
        return df > 0 ? (SumSquares(first) + SumSquares(second)) / df : 0;
    }

    private static double SumSquares(IReadOnlyList<double> v)
    {
        double mean = v.Average();
        return v.Sum(x => (x - mean) * (x - mean));
    }

    /// <summary>
    /// t de Student bilateral, com variância combinada: t = (média2 - média1) / (s · raiz(1/n1 + 1/n2)), gl = n1+n2-2.
    /// p = I_x(gl/2, 1/2), com x = gl / (gl + t²) e I a função beta incompleta regularizada.
    /// </summary>
    internal static double StudentTP(IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        int df = first.Count + second.Count - 2;
        double diff = second.Average() - first.Average();
        double se = Math.Sqrt(PooledVariance(first, second) * (1.0 / first.Count + 1.0 / second.Count));
        if (se == 0) return diff == 0 ? 1 : 0;
        return StudentTwoSidedP(diff / se, df);
    }

    internal static double StudentTwoSidedP(double t, int df) =>
        RegularizedBeta(df / (df + t * t), df / 2.0, 0.5);

    /// <summary>
    /// Mann-Whitney bilateral: U1 = R1 - n1(n1+1)/2, com R1 a soma dos postos da primeira amostra (empates recebem o posto médio).
    /// Até 40 valores, p exato: conta quantas das C(N, n1) escolhas de postos dão soma tão extrema quanto a observada.
    /// Acima disso, aproximação normal com correção de empates e de continuidade.
    /// </summary>
    internal static double MannWhitneyP(IReadOnlyList<double> first, IReadOnlyList<double> second)
    {
        var doubledRanks = DoubledRanks(first.Concat(second).ToArray()); // posto × 2: o posto médio de um empate vira inteiro
        int observed = doubledRanks.Take(first.Count).Sum();
        return doubledRanks.Length <= ExactRankLimit
            ? ExactRankP(doubledRanks, first.Count, observed)
            : NormalRankP(doubledRanks, first.Count, observed);
    }

    private static int[] DoubledRanks(double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var ranks = new int[values.Length];
        for (int i = 0; i < order.Length;)
        {
            int j = i;
            while (j + 1 < order.Length && values[order[j + 1]] == values[order[i]]) j++;
            for (int k = i; k <= j; k++) ranks[order[k]] = i + j + 2; // (i+1 + j+1) / 2, vezes 2
            i = j + 1;
        }
        return ranks;
    }

    private static double ExactRankP(int[] doubledRanks, int n1, int observed)
    {
        int maxSum = doubledRanks.Sum();
        var ways = new double[n1 + 1, maxSum + 1]; // ways[k, s]: quantas escolhas de k postos somam s
        ways[0, 0] = 1;
        foreach (int rank in doubledRanks)
            for (int k = n1; k >= 1; k--)
                for (int s = maxSum; s >= rank; s--)
                    ways[k, s] += ways[k - 1, s - rank];

        double total = 0, atMost = 0, atLeast = 0;
        for (int s = 0; s <= maxSum; s++)
        {
            double w = ways[n1, s];
            total += w;
            if (s <= observed) atMost += w;
            if (s >= observed) atLeast += w;
        }
        return Math.Min(1, 2 * Math.Min(atMost, atLeast) / total);
    }

    private static double NormalRankP(int[] doubledRanks, int n1, int observed)
    {
        int n = doubledRanks.Length, n2 = n - n1;
        double u = observed / 2.0 - n1 * (n1 + 1) / 2.0;
        double mean = n1 * (double)n2 / 2;
        double ties = doubledRanks.GroupBy(r => r).Sum(g => Math.Pow(g.Count(), 3) - g.Count());
        double variance = n1 * (double)n2 / 12 * (n + 1 - ties / (n * (n - 1.0)));
        if (variance <= 0) return 1;
        double z = Math.Max(0, Math.Abs(u - mean) - 0.5) / Math.Sqrt(variance);
        return Math.Min(1, Erfc(z / Math.Sqrt(2)));
    }

    private static double Binomial(int n, int k)
    {
        double r = 1;
        for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }

    /// <summary>Função erro complementar para z ≥ 0 (Numerical Recipes, erro relativo abaixo de 1,2e-7).</summary>
    private static double Erfc(double z)
    {
        double t = 1 / (1 + 0.5 * z);
        return t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
               t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
    }

    /// <summary>I_x(a, b) por fração contínua (Lentz). A simetria I_x(a, b) = 1 - I_(1-x)(b, a) mantém a série na região em que converge.</summary>
    private static double RegularizedBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double front = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        return x < (a + 1) / (a + b + 2)
            ? front * BetaFraction(x, a, b) / a
            : 1 - front * BetaFraction(1 - x, b, a) / b;
    }

    private static double BetaFraction(double x, double a, double b)
    {
        const double Tiny = 1e-300, Epsilon = 1e-14;
        static double Guard(double v) => Math.Abs(v) < Tiny ? Tiny : v;
        double c = 1, d = 1 / Guard(1 - (a + b) * x / (a + 1)), h = d;
        for (int m = 1; m <= 500; m++)
        {
            int m2 = 2 * m;
            double even = m * (b - m) * x / ((a + m2 - 1) * (a + m2));
            d = 1 / Guard(1 + even * d);
            c = Guard(1 + even / c);
            h *= d * c;
            double odd = -(a + m) * (a + b + m) * x / ((a + m2) * (a + m2 + 1));
            d = 1 / Guard(1 + odd * d);
            c = Guard(1 + odd / c);
            double step = d * c;
            h *= step;
            if (Math.Abs(step - 1) < Epsilon) break;
        }
        return h;
    }

    /// <summary>Logaritmo da função gama (Lanczos).</summary>
    private static double LogGamma(double x)
    {
        double[] coefficients = { 76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5 };
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double series = 1.000000000190015;
        foreach (double c in coefficients) series += c / ++y;
        return -tmp + Math.Log(2.5066282746310005 * series / x);
    }
}
