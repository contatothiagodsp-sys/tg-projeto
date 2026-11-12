namespace ThgOptimization.Core.Intelligence;

/// <summary>
/// Estatística robusta usada pelo motor: mediana, MAD, percentil, tendência (Theil–Sen) e bootstrap. Tudo determinístico:
/// o bootstrap usa semente fixa, para o mesmo conjunto de dados dar sempre a mesma resposta (e os testes serem estáveis).
/// </summary>
public static class RobustStats
{
    /// <summary>Fator que torna o MAD comparável ao desvio padrão numa distribuição normal.</summary>
    public const double MadScale = 1.4826;

    public static double Median(IEnumerable<double> values)
    {
        var a = values.ToArray();
        if (a.Length == 0) return double.NaN;
        Array.Sort(a);
        return MedianSorted(a);
    }

    public static double MedianSorted(ReadOnlySpan<double> sorted) =>
        sorted.Length == 0 ? double.NaN
        : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
        : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

    /// <summary>Percentil com interpolação linear sobre um vetor ordenado. p entre 0 e 1.</summary>
    public static double PercentileSorted(ReadOnlySpan<double> sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        double pos = Math.Clamp(p, 0, 1) * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos), hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var a = values.ToArray();
        Array.Sort(a);
        return PercentileSorted(a, p);
    }

    /// <summary>Desvio absoluto mediano, já na escala do desvio padrão.</summary>
    public static double Mad(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return double.NaN;
        double m = Median(values);
        return MadScale * Median(values.Select(v => Math.Abs(v - m)));
    }

    /// <summary>
    /// Distância de um valor ao centro, em desvios robustos. O piso evita dividir por um MAD quase zero (série muito estável),
    /// que transformaria qualquer diferença mínima numa "anomalia": o piso é relativo ao centro e nunca menor que absoluteFloor.
    /// </summary>
    public static double RobustZ(double value, double center, double spread, double relativeFloor = 0.02, double absoluteFloor = 1e-9)
    {
        double floor = Math.Max(Math.Abs(center) * relativeFloor, absoluteFloor);
        double s = double.IsNaN(spread) ? floor : Math.Max(spread, floor);
        return (value - center) / s;
    }

    /// <summary>Inclinação de Theil–Sen (mediana das inclinações entre pares): uma tendência que um pico isolado não move. Unidade: y por x.</summary>
    public static double TheilSenSlope(IReadOnlyList<double> x, IReadOnlyList<double> y, int maxPairs = 20_000)
    {
        int n = Math.Min(x.Count, y.Count);
        if (n < 3) return double.NaN;
        // Com muitos pontos, salta de forma regular: a mediana das inclinações continua representativa e a conta não é quadrática.
        long all = (long)n * (n - 1) / 2;
        int step = all <= maxPairs ? 1 : (int)Math.Ceiling(Math.Sqrt((double)all / maxPairs));
        var slopes = new List<double>();
        for (int i = 0; i < n; i += step)
            for (int j = i + step; j < n; j += step)
            {
                double dx = x[j] - x[i];
                if (dx != 0) slopes.Add((y[j] - y[i]) / dx);
            }
        return slopes.Count == 0 ? double.NaN : Median(slopes);
    }

    /// <summary>Correlação de postos (Spearman). NaN sem pontos suficientes ou sem variação.</summary>
    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = Math.Min(x.Count, y.Count);
        if (n < 5) return double.NaN;
        var rx = Ranks(x, n);
        var ry = Ranks(y, n);
        double mx = rx.Average(), my = ry.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = rx[i] - mx, dy = ry[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxx <= 0 || syy <= 0 ? double.NaN : sxy / Math.Sqrt(sxx * syy);
    }

    private static double[] Ranks(IReadOnlyList<double> v, int n)
    {
        var order = Enumerable.Range(0, n).OrderBy(i => v[i]).ToArray();
        var ranks = new double[n];
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j + 1 < n && v[order[j + 1]] == v[order[i]]) j++;
            double rank = (i + j) / 2.0 + 1; // empates recebem o posto médio
            for (int k = i; k <= j; k++) ranks[order[k]] = rank;
            i = j + 1;
        }
        return ranks;
    }

    /// <summary>Intervalo de confiança de uma diferença relativa de medianas (candidato sobre referência), em %.</summary>
    public readonly record struct Interval(double Low, double High, double Point)
    {
        public bool ExcludesZero => Low > 0 || High < 0;
        public double HalfWidth => (High - Low) / 2;
    }

    /// <summary>
    /// Bootstrap percentil da diferença relativa de medianas: (mediana(candidato) − mediana(referência)) ÷ mediana(referência) × 100.
    /// Com menos de duas medidas de algum lado não há variação para reamostrar: devolve null, e quem chama declara "inconclusivo".
    /// </summary>
    public static Interval? BootstrapMedianDeltaPct(IReadOnlyList<double> baseline, IReadOnlyList<double> candidate,
                                                    double confidence = 0.95, int resamples = 2000, int seed = 20_000)
    {
        if (baseline.Count < 2 || candidate.Count < 2) return null;
        double mb = Median(baseline);
        if (double.IsNaN(mb) || mb == 0) return null;
        double point = (Median(candidate) - mb) / mb * 100;
        var rng = new Random(seed);
        var deltas = new double[resamples];
        var rb = new double[baseline.Count];
        var rc = new double[candidate.Count];
        for (int r = 0; r < resamples; r++)
        {
            for (int i = 0; i < rb.Length; i++) rb[i] = baseline[rng.Next(baseline.Count)];
            for (int i = 0; i < rc.Length; i++) rc[i] = candidate[rng.Next(candidate.Count)];
            Array.Sort(rb);
            Array.Sort(rc);
            double b = MedianSorted(rb);
            deltas[r] = b == 0 ? 0 : (MedianSorted(rc) - b) / b * 100;
        }
        Array.Sort(deltas);
        double tail = (1 - confidence) / 2;
        return new Interval(PercentileSorted(deltas, tail), PercentileSorted(deltas, 1 - tail), point);
    }

    /// <summary>Variação normal de uma medida repetida nas mesmas condições, em % da mediana (MAD ÷ mediana). NaN com menos de 3 medidas.</summary>
    public static double NoisePct(IReadOnlyCollection<double> repeats)
    {
        if (repeats.Count < 3) return double.NaN;
        double m = Median(repeats);
        return m == 0 ? double.NaN : Mad(repeats) / Math.Abs(m) * 100;
    }
}
