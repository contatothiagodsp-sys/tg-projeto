using System.Globalization;
using System.IO;
using System.Text;

namespace ThgOptimization;

/// <summary>Resultado de uma gravação de frames, com as métricas usadas por CapFrameX e PresentMon.</summary>
public sealed record CaptureResult(string Game, DateTime Start, double Seconds, int Frames, double AvgFps, double Low1Fps, double Low01Fps,
                                   double P99Ms, double MaxMs, int Stutters, string? CsvPath = null)
{
    public string Summary => $"{AvgFps:0} FPS · 1% {Low1Fps:0} · 0,1% {Low01Fps:0} · {Stutters} engasgo(s)";
}

public static class FrameAnalysis
{
    /// <summary>
    /// Métricas a partir dos instantes de cada frame (ms):
    /// FPS médio = frames ÷ tempo; 1% e 0,1% low = FPS equivalente à média do 1% e do 0,1% de frames mais lentos (como o CapFrameX);
    /// engasgo = frame que demora mais que 2,5× a mediana e mais de 25 ms (aparece na tela como travadinha).
    /// </summary>
    public static CaptureResult Analyze(string game, DateTime start, List<double> times)
    {
        if (times.Count < 3) return new CaptureResult(game, start, 0, times.Count, 0, 0, 0, 0, 0, 0);
        var ft = new double[times.Count - 1];
        for (int i = 1; i < times.Count; i++) ft[i - 1] = times[i] - times[i - 1];
        double seconds = (times[^1] - times[0]) / 1000.0;
        var sorted = ft.OrderBy(x => x).ToArray();
        double Pct(double p) => sorted[Math.Clamp((int)Math.Floor(sorted.Length * p), 0, sorted.Length - 1)];
        double LowAvg(double fraction)
        {
            int n = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction));
            double mean = sorted.Skip(sorted.Length - n).Average();
            return mean > 0 ? 1000 / mean : 0;
        }
        double median = Pct(0.5);
        int stutters = ft.Count(x => x > median * 2.5 && x > 25);
        return new CaptureResult(game, start, Math.Round(seconds, 1), ft.Length, ft.Length / Math.Max(0.001, seconds),
                                 LowAvg(0.01), LowAvg(0.001), Pct(0.99), sorted[^1], stutters);
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
