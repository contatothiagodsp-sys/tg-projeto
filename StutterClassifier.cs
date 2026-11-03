namespace ThgOptimization;

/// <summary>
/// Métricas de uma lista de frametimes (ms). Percentil pelo método do posto mais próximo: o menor valor com pelo menos p% dos frames
/// abaixo ou iguais. Há dois "lows", porque são contas diferentes: Percentile = FPS equivalente ao percentil; Avg = FPS da média dos frames
/// mais lentos (como o CapFrameX). Média de FPS e FPS do percentil não são a mesma coisa e não aparecem com o mesmo nome.
/// </summary>
public sealed record FrameStatistics(int Frames, double Seconds, double AvgFps, double InstantFps,
                                     double Low1PercentileFps, double Low01PercentileFps, double Low1AvgFps, double Low01AvgFps,
                                     double AvgFrameTimeMs, double P50Ms, double P90Ms, double P95Ms, double P99Ms, double P999Ms, double MaxMs,
                                     int Stutters, int[] Histogram)
{
    /// <summary>Limite superior (ms) de cada faixa do histograma. A última faixa é o que passa de 100 ms.</summary>
    public static readonly double[] HistogramLimits = { 4, 8.34, 16.67, 33.34, 50, 100 };

    /// <summary>Engasgo: mais que 2,5 × a mediana e mais de 25 ms. É o critério que as sessões salvas já usam.</summary>
    public const double StutterMedianFactor = 2.5, StutterMinMs = 25;

    /// <summary>Posto mais próximo sobre um vetor já ordenado.</summary>
    public static double Percentile(ReadOnlySpan<double> sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];

    /// <summary>FPS da média da fração mais lenta dos frames, sobre um vetor já ordenado.</summary>
    public static double LowAverageFps(ReadOnlySpan<double> sorted, double fraction)
    {
        if (sorted.Length == 0) return 0;
        int n = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction));
        double sum = 0;
        for (int i = sorted.Length - n; i < sorted.Length; i++) sum += sorted[i];
        double mean = sum / n;
        return mean > 0 ? 1000 / mean : 0;
    }

    /// <summary>Calcula tudo a partir dos frametimes. Ordena o vetor recebido no lugar: quem chama passa um buffer próprio.</summary>
    public static FrameStatistics FromFrameTimes(Span<double> frameTimes, double instantFps = 0)
    {
        if (frameTimes.Length == 0) return new FrameStatistics(0, 0, 0, instantFps, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new int[HistogramLimits.Length + 1]);
        double sum = 0;
        var histogram = new int[HistogramLimits.Length + 1];
        foreach (double ft in frameTimes)
        {
            sum += ft;
            int bucket = 0;
            while (bucket < HistogramLimits.Length && ft > HistogramLimits[bucket]) bucket++;
            histogram[bucket]++;
        }
        frameTimes.Sort();
        ReadOnlySpan<double> s = frameTimes;
        double p50 = Percentile(s, 0.50), p99 = Percentile(s, 0.99), p999 = Percentile(s, 0.999);
        double limit = Math.Max(p50 * StutterMedianFactor, StutterMinMs);
        int stutters = 0;
        for (int i = s.Length - 1; i >= 0 && s[i] > limit; i--) stutters++;
        return new FrameStatistics(s.Length, sum / 1000, sum > 0 ? s.Length * 1000 / sum : 0, instantFps,
                                   p99 > 0 ? 1000 / p99 : 0, p999 > 0 ? 1000 / p999 : 0, LowAverageFps(s, 0.01), LowAverageFps(s, 0.001),
                                   sum / s.Length, p50, Percentile(s, 0.90), Percentile(s, 0.95), p99, p999, s[^1], stutters, histogram);
    }

    public double StuttersPerMinute => Seconds > 0 ? Stutters * 60 / Seconds : 0;
}

/// <summary>Critérios da classificação. Todos configuráveis, e cada um é uma conta sobre os frametimes.</summary>
/// <param name="HighFrametimeMs">Frametime elevado: acima disso (33,34 ms = abaixo de 30 FPS).</param>
/// <param name="SpikeMedianFactor">Pico: acima de SpikeMedianFactor × mediana e de SpikeMinMs.</param>
/// <param name="RecurrentWindowSeconds">Stutter recorrente: RecurrentCount picos ou mais dentro desta janela.</param>
/// <param name="OscillationWindowFrames">Oscilação persistente: OscillationWindows janelas seguidas com desvio ÷ média acima de OscillationCv.</param>
/// <param name="DropFactor">Queda de FPS: um segundo com FPS médio abaixo de DropFactor × o FPS típico da sessão.</param>
/// <param name="MemoryLowPercent">Possível pressão de memória: menos que isso de RAM disponível até MemoryNearSeconds de um pico.</param>
public sealed record StutterCriteria(double HighFrametimeMs = 33.34, double SpikeMedianFactor = FrameStatistics.StutterMedianFactor, double SpikeMinMs = FrameStatistics.StutterMinMs,
                                     double RecurrentWindowSeconds = 10, int RecurrentCount = 3, int OscillationWindowFrames = 120, int OscillationWindows = 3,
                                     double OscillationCv = 0.35, double DropFactor = 0.7, double MemoryLowPercent = 10, double MemoryNearSeconds = 5);

public enum StutterVerdict
{
    /// <summary>Nenhum pico pelos critérios.</summary>
    None,
    /// <summary>Houve engasgo, e os dados não apontam uma causa. É a resposta honesta na maioria dos casos.</summary>
    DetectedCauseUnknown,
    /// <summary>Houve engasgo junto de uma condição medida que pode explicá-lo. É associação, não prova.</summary>
    DetectedPossibleCause,
}

/// <summary>Uma leitura da RAM disponível durante a sessão. AtMs usa o mesmo relógio dos frames.</summary>
public readonly record struct MemorySample(double AtMs, double AvailablePercent);

public sealed record StutterReport(StutterVerdict Verdict, int HighFrametimeFrames, int Spikes, int IsolatedSpikes, int RecurrentEpisodes,
                                   int OscillationEpisodes, int FpsDropSeconds, bool PossibleMemoryPressure)
{
    public string Summary
    {
        get
        {
            if (Verdict == StutterVerdict.None) return "Sem engasgos pelos critérios atuais.";
            var parts = new List<string>();
            if (IsolatedSpikes > 0) parts.Add($"{IsolatedSpikes} pico(s) isolado(s)");
            if (RecurrentEpisodes > 0) parts.Add($"{RecurrentEpisodes} período(s) de engasgo recorrente");
            if (OscillationEpisodes > 0) parts.Add($"{OscillationEpisodes} período(s) de oscilação");
            if (FpsDropSeconds > 0) parts.Add($"{FpsDropSeconds} s de queda de FPS");
            string head = Verdict == StutterVerdict.DetectedPossibleCause
                ? "STUTTER ASSOCIADO A UMA POSSÍVEL CAUSA: pouca RAM disponível perto dos picos"
                : "STUTTER DETECTADO, CAUSA NÃO IDENTIFICADA";
            return parts.Count > 0 ? $"{head} · {string.Join(", ", parts)}" : head;
        }
    }
}

/// <summary>
/// Classifica os engasgos de uma gravação, depois de ela terminar. Só afirma causa quando há medida da condição: DPC, ISR e driver
/// não entram aqui, porque nada nestes dados os observa.
/// </summary>
public static class StutterClassifier
{
    private const int IsolationFrames = 5;

    /// <param name="times">Instante de cada frame (ms), em ordem.</param>
    public static StutterReport Classify(IReadOnlyList<double> times, StutterCriteria? criteria = null, IReadOnlyList<MemorySample>? memory = null)
    {
        var c = criteria ?? new StutterCriteria();
        if (times.Count < 3) return new StutterReport(StutterVerdict.None, 0, 0, 0, 0, 0, 0, false);
        var ft = new double[times.Count - 1];
        for (int i = 0; i < ft.Length; i++) ft[i] = times[i + 1] - times[i];
        var sorted = (double[])ft.Clone();
        Array.Sort(sorted);
        double median = FrameStatistics.Percentile(sorted, 0.5);
        double spikeLimit = Math.Max(c.SpikeMedianFactor * median, c.SpikeMinMs);

        int high = 0;
        var spikes = new List<int>();
        for (int i = 0; i < ft.Length; i++)
        {
            if (ft[i] > c.HighFrametimeMs) high++;
            if (ft[i] > spikeLimit) spikes.Add(i);
        }

        // Recorrente: janelas que não se sobrepõem, a partir do primeiro pico de cada grupo.
        var inEpisode = new bool[spikes.Count];
        int episodes = 0;
        double window = c.RecurrentWindowSeconds * 1000;
        for (int s = 0; s < spikes.Count;)
        {
            int e = s;
            while (e + 1 < spikes.Count && times[spikes[e + 1] + 1] - times[spikes[s] + 1] <= window) e++;
            if (e - s + 1 >= c.RecurrentCount)
            {
                episodes++;
                for (int k = s; k <= e; k++) inEpisode[k] = true;
                s = e + 1;
            }
            else s++;
        }

        int isolated = 0;
        for (int s = 0; s < spikes.Count; s++)
        {
            if (inEpisode[s]) continue;
            bool closeBefore = s > 0 && spikes[s] - spikes[s - 1] <= IsolationFrames;
            bool closeAfter = s + 1 < spikes.Count && spikes[s + 1] - spikes[s] <= IsolationFrames;
            if (!closeBefore && !closeAfter) isolated++;
        }

        bool memoryNear = false;
        if (memory is { Count: > 0 } && spikes.Count > 0)
        {
            double near = c.MemoryNearSeconds * 1000;
            foreach (var sample in memory)
            {
                if (sample.AvailablePercent >= c.MemoryLowPercent) continue;
                foreach (int s in spikes)
                    if (Math.Abs(times[s + 1] - sample.AtMs) <= near) { memoryNear = true; break; }
                if (memoryNear) break;
            }
        }

        int oscillation = CountOscillation(ft, c);
        int drops = CountFpsDrops(times, median, c);
        bool detected = spikes.Count > 0 || episodes > 0 || oscillation > 0 || drops > 0;
        var verdict = !detected ? StutterVerdict.None : memoryNear ? StutterVerdict.DetectedPossibleCause : StutterVerdict.DetectedCauseUnknown;
        return new StutterReport(verdict, high, spikes.Count, isolated, episodes, oscillation, drops, memoryNear);
    }

    private static int CountOscillation(double[] ft, StutterCriteria c)
    {
        int episodes = 0, run = 0;
        for (int start = 0; start + c.OscillationWindowFrames <= ft.Length; start += c.OscillationWindowFrames)
        {
            double sum = 0, squares = 0;
            for (int i = start; i < start + c.OscillationWindowFrames; i++) { sum += ft[i]; squares += ft[i] * ft[i]; }
            double mean = sum / c.OscillationWindowFrames;
            double variance = Math.Max(0, squares / c.OscillationWindowFrames - mean * mean);
            if (mean > 0 && Math.Sqrt(variance) / mean > c.OscillationCv)
            {
                if (++run == c.OscillationWindows) episodes++;
            }
            else run = 0;
        }
        return episodes;
    }

    /// <summary>Segundos com FPS abaixo do esperado: o frametime subiu junto, porque é o mesmo dado visto por segundo.</summary>
    private static int CountFpsDrops(IReadOnlyList<double> times, double median, StutterCriteria c)
    {
        if (median <= 0) return 0;
        double typicalFps = 1000 / median, start = times[0];
        int drops = 0, frames = 0;
        for (int i = 1; i < times.Count; i++)
        {
            frames++;
            if (times[i] - start < 1000) continue;
            if (frames * 1000 / (times[i] - start) < typicalFps * c.DropFactor) drops++;
            start = times[i];
            frames = 0;
        }
        return drops;
    }
}
