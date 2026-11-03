using System.Globalization;
using System.IO;
using System.Text;

namespace ThgOptimization;

/// <summary>Resultado de uma gravação de frames, com as métricas usadas por CapFrameX e PresentMon.</summary>
public sealed record CaptureResult(string Game, DateTime Start, double Seconds, int Frames, double AvgFps, double Low1Fps, double Low01Fps,
                                   double P99Ms, double MaxMs, int Stutters, string? CsvPath = null,
                                   FrameStatistics? Statistics = null, StutterReport? Diagnosis = null, long DroppedFrames = 0)
{
    public string Summary => $"{AvgFps:0} FPS · 1% {Low1Fps:0} · 0,1% {Low01Fps:0} · {Stutters} engasgo(s)";

    /// <summary>Percentis, classificação dos engasgos e aviso de gravação cortada. Vazio em gravações de versões antigas.</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Statistics is { } s) parts.Add($"P50 {s.P50Ms:0.0} · P95 {s.P95Ms:0.0} · P99 {s.P99Ms:0.0} · P99,9 {s.P999Ms:0.0} ms");
            if (Diagnosis is { } d) parts.Add(d.Summary);
            if (DroppedFrames > 0) parts.Add($"{DroppedFrames} frames não couberam na gravação: o fim da sessão ficou de fora");
            return string.Join(" · ", parts);
        }
    }
}

public static class FrameAnalysis
{
    /// <summary>
    /// Métricas a partir dos instantes de cada frame (ms):
    /// FPS médio = frames ÷ tempo; 1% e 0,1% low = FPS equivalente à média do 1% e do 0,1% de frames mais lentos (como o CapFrameX);
    /// engasgo = frame que demora mais que 2,5× a mediana e mais de 25 ms (aparece na tela como travadinha).
    /// </summary>
    public static CaptureResult Analyze(string game, DateTime start, List<double> times, long droppedFrames = 0, IReadOnlyList<MemorySample>? memory = null)
    {
        if (times.Count < 3) return new CaptureResult(game, start, 0, times.Count, 0, 0, 0, 0, 0, 0);
        // Uma sessão longa tem milhões de frames: ordena no lugar, sem segunda cópia. Nada abaixo depende da ordem original.
        var sorted = new double[times.Count - 1];
        for (int i = 1; i < times.Count; i++) sorted[i - 1] = times[i] - times[i - 1];
        double seconds = (times[^1] - times[0]) / 1000.0;
        Array.Sort(sorted);
        var statistics = FrameStatistics.FromFrameTimes(sorted);
        return new CaptureResult(game, start, Math.Round(seconds, 1), sorted.Length, sorted.Length / Math.Max(0.001, seconds),
                                 statistics.Low1AvgFps, statistics.Low01AvgFps, statistics.P99Ms, statistics.MaxMs, statistics.Stutters,
                                 Statistics: statistics, Diagnosis: StutterClassifier.Classify(times, memory: memory), DroppedFrames: droppedFrames);
    }

    public static string CaptureFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "tHgOptimization", "Gravacoes");

    /// <summary>Salva frame a frame (como o PresentMon/CapFrameX exportam) para abrir no Excel ou comparar depois.</summary>
    public static string SaveCsv(CaptureResult r, List<double> times)
    {
        Directory.CreateDirectory(CaptureFolder);
        string safe = string.Concat(r.Game.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(CaptureFolder, $"{safe}-{r.Start:yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder("frame;tempo_ms;frametime_ms;fps\r\n");
        for (int i = 1; i < times.Count; i++)
        {
            double ftm = times[i] - times[i - 1];
            sb.Append(i).Append(';').Append((times[i] - times[0]).ToString("0.000", CultureInfo.InvariantCulture)).Append(';')
              .Append(ftm.ToString("0.000", CultureInfo.InvariantCulture)).Append(';')
              .Append((ftm > 0 ? 1000 / ftm : 0).ToString("0.0", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>Todas as sessões e gravações em uma planilha (uma linha por sessão).</summary>
    public static string ExportSessions(IEnumerable<GameSessionRecord> sessions, IEnumerable<CaptureResult> captures)
    {
        Directory.CreateDirectory(CaptureFolder);
        string path = Path.Combine(CaptureFolder, $"sessoes-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        var sb = new StringBuilder("tipo;jogo;inicio;minutos;fps_medio;low_1;low_01;engasgos;depois_do_turbo\r\n");
        foreach (var s in sessions)
            sb.Append($"sessao;{s.Game};{s.Start:yyyy-MM-dd HH:mm};{F(s.Minutes)};{F(s.AvgFps)};{F(s.Low1Fps)};{F(s.Low01Fps)};{s.Stutters};{(s.AfterTurbo ? "sim" : "não")}\r\n");
        foreach (var c in captures)
            sb.Append($"gravacao;{c.Game};{c.Start:yyyy-MM-dd HH:mm};{F(c.Seconds / 60)};{F(c.AvgFps)};{F(c.Low1Fps)};{F(c.Low01Fps)};{c.Stutters};\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }
}
