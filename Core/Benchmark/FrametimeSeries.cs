using System.Globalization;
using System.IO;

namespace ThgOptimization.Core.Benchmark;

public readonly record struct FramePoint(double Seconds, double Ms);

/// <summary>Curva de frametime para o gráfico: lida do CSV da captura e reduzida ao que cabe na tela.</summary>
public static class FrametimeSeries
{
    public const int MaxPoints = 600;

    /// <summary>Lê o CSV de FrameAnalysis.SaveCsv. Arquivo apagado ou ilegível devolve vazio: o gráfico avisa, e os números continuam.</summary>
    public static List<FramePoint> Load(string? csvPath)
    {
        var points = new List<FramePoint>();
        if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath)) return points;
        try
        {
            foreach (string line in File.ReadLines(csvPath).Skip(1))
            {
                var c = line.Split(';');
                if (c.Length >= 3 &&
                    double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) &&
                    double.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double frametime))
                    points.Add(new FramePoint(ms / 1000, frametime));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new List<FramePoint>(); }
        return points;
    }

    /// <summary>Fica o pior frame de cada faixa: uma média apagaria justamente os engasgos que o gráfico existe para mostrar.</summary>
    public static List<FramePoint> Downsample(IReadOnlyList<FramePoint> all, int maxPoints = MaxPoints)
    {
        if (all.Count <= maxPoints) return all.ToList();
        var reduced = new List<FramePoint>(maxPoints);
        for (int bucket = 0; bucket < maxPoints; bucket++)
        {
            int from = (int)((long)bucket * all.Count / maxPoints), to = (int)((long)(bucket + 1) * all.Count / maxPoints);
            var worst = all[from];
            for (int i = from + 1; i < to; i++)
                if (all[i].Ms > worst.Ms) worst = all[i];
            reduced.Add(worst);
        }
        return reduced;
    }

    /// <summary>Eixo invertido: 0 ms fica no topo, então a curva mais alta é a melhor. O que passa do teto fica na borda de baixo.</summary>
    public static double PlotY(double ms, double maxMs, double height) => Math.Clamp(ms / maxMs, 0, 1) * height;

    /// <summary>Teto do eixo Y: o P99,9 com folga, para um engasgo isolado não achatar o resto da curva.</summary>
    public static double AxisMaxMs(AbComparison r)
    {
        double top = Math.Max(Math.Max(r.A.P999Ms, r.B.P999Ms), Math.Max(r.A.P99Ms, r.B.P99Ms));
        return top > 0 ? top * 1.25 : 50;
    }
}

/// <summary>Uma linha da tabela A contra B, a mesma no painel do gráfico e no relatório.</summary>
public sealed record AbRow(string Metric, string A, string B, double DeltaPct, bool HigherIsBetter)
{
    public string Delta => DeltaPct.ToString("+0.0;-0.0;0.0", AbTable.PtBr) + "%";
    /// <summary>Null: sem mudança que valha uma cor.</summary>
    public bool? IsBetter => Math.Abs(DeltaPct) < 0.05 ? null : DeltaPct > 0 == HigherIsBetter;
}

public static class AbTable
{
    internal static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static IReadOnlyList<AbRow> Rows(AbComparison r)
    {
        AbRow Row(string name, Func<BenchmarkSession, double> value, string format, string unit, bool higherIsBetter)
        {
            var (baseline, candidate) = r.BaselineIsA ? (r.A, r.B) : (r.B, r.A);
            string Text(BenchmarkSession s) => value(s).ToString(format, PtBr) + unit;
            return new AbRow(name, Text(r.A), Text(r.B), FrameTimeStatistics.DeltaPct(value(baseline), value(candidate)), higherIsBetter);
        }
        return new[]
        {
            Row("FPS médio", s => s.AvgFps, "0.0", "", true),
            Row("1% low", s => s.Low1Fps, "0.0", "", true),
            Row("0,1% low", s => s.Low01Fps, "0.0", "", true),
            Row("Engasgos", s => s.StutterPct, "0.00", "%", false),
            Row("P99", s => s.P99Ms, "0.0", " ms", false),
            Row("P99,9", s => s.P999Ms, "0.0", " ms", false),
        };
    }
}
