using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using ThgOptimization.Core.Benchmark;
using ThgOptimization.Views;

namespace ThgOptimization.Tests;

public class FrametimeSeriesTests
{
    [Fact]
    public void Downsample_keeps_the_stutter_a_mean_would_erase()
    {
        var all = Enumerable.Range(0, 20_000).Select(i => new FramePoint(i / 300.0, i == 12_345 ? 90 : 3.3)).ToList();
        var reduced = FrametimeSeries.Downsample(all);
        Assert.Equal(FrametimeSeries.MaxPoints, reduced.Count);
        Assert.Equal(90, reduced.Max(p => p.Ms));
    }

    [Fact]
    public void Short_series_is_not_touched()
    {
        var all = new List<FramePoint> { new(0, 5), new(1, 6) };
        Assert.Equal(all, FrametimeSeries.Downsample(all));
    }

    [Fact]
    public void Y_axis_is_inverted_and_clamped()
    {
        Assert.Equal(0, FrametimeSeries.PlotY(0, 50, 200));
        Assert.Equal(100, FrametimeSeries.PlotY(25, 50, 200));
        Assert.Equal(200, FrametimeSeries.PlotY(500, 50, 200));
    }

    [Fact]
    public void Csv_written_by_the_capture_is_read_back()
    {
        string game = "lab-test-" + Guid.NewGuid().ToString("N");
        string path = FrameAnalysis.SaveCsv(new CaptureResult(game, DateTime.Now, 1, 2, 80, 60, 50, 15, 15, 0), new List<double> { 0, 10, 25 });
        try { Assert.Equal(new[] { new FramePoint(0.01, 10), new FramePoint(0.025, 15) }, FrametimeSeries.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_csv_gives_an_empty_series()
    {
        Assert.Empty(FrametimeSeries.Load(null));
        Assert.Empty(FrametimeSeries.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Table_colors_each_metric_in_its_own_direction()
    {
        var a = new BenchmarkSession("j", "A", DateTime.Now, 100, 70, 50, 20, 2, 10, 6000, 60, null) { P999Ms = 30 };
        var b = a with { AvgFps = 110, StutterPct = 1, P99Ms = 25, P999Ms = 30 };
        var rows = AbTable.Rows(FrameTimeStatistics.Compare(a, b, aIsBaseline: true)).ToDictionary(r => r.Metric);
        Assert.True(rows["FPS médio"].IsBetter);
        Assert.Equal("+10,0%", rows["FPS médio"].Delta);
        Assert.True(rows["Engasgos"].IsBetter); // caiu: melhorou
        Assert.False(rows["P99"].IsBetter);     // subiu: piorou
        Assert.Null(rows["P99,9"].IsBetter);
    }
}

[Collection(WpfCollection.Name)]
public class FrametimeChartTests
{
    internal static AbComparison Sample()
    {
        var a = new BenchmarkSession("cs2", "Padrão", DateTime.Now, 142, 96, 71, 11.2, 0.31, 7, 8500, 60, null) { P999Ms = 18, Slot = "A" };
        var b = a with { ConfigLabel = "XMP ativo", AvgFps = 158, Low1Fps = 118, Low01Fps = 92, P99Ms = 8.9, StutterPct = 0.12, P999Ms = 13, Slot = "B" };
        return FrameTimeStatistics.Compare(new[] { a, a with { AvgFps = 141 }, a with { AvgFps = 144 } },
                                           new[] { b, b with { AvgFps = 157 }, b with { AvgFps = 160 } }, aIsBaseline: true);
    }

    internal static List<FramePoint> Curve(double baseMs, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, 8000)
            .Select(i => new FramePoint(i * 60.0 / 8000, baseMs + random.NextDouble() * 2 + (random.Next(400) == 0 ? baseMs * 1.5 : 0)))
            .ToList();
    }

    internal static void EnsureTheme()
    {
        if (Application.Current != null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/tHgOptimization;component/Theme.xaml") });
    }

    [Fact]
    public void Chart_draws_both_curves_and_the_four_markers()
    {
        Sta.Run(() =>
        {
            EnsureTheme();
            var chart = new FrametimeCompareChart();
            chart.Show(Sample(), Curve(7, 1), Curve(6, 2));
            chart.Measure(new Size(1000, 340));
            chart.Arrange(new Rect(0, 0, 1000, 340));
            chart.UpdateLayout();
            var plot = (Canvas)chart.FindName("Plot");
            Assert.Equal(2, plot.Children.OfType<Polyline>().Count());
            Assert.Equal(4, plot.Children.OfType<Line>().Count(l => l.StrokeDashArray is { Count: > 0 }));
            Assert.All(plot.Children.OfType<Polyline>(), l => Assert.True(l.Stroke.IsFrozen));
        });
    }

    [Fact]
    public void Without_the_csv_the_chart_says_so_and_keeps_the_numbers()
    {
        Sta.Run(() =>
        {
            EnsureTheme();
            var chart = new FrametimeCompareChart();
            chart.Show(Sample(), Array.Empty<FramePoint>(), Array.Empty<FramePoint>());
            Assert.Equal(Visibility.Visible, ((FrameworkElement)chart.FindName("NoData")).Visibility);
            Assert.True(((StackPanel)chart.FindName("Numbers")).Children.Count > 3);
        });
    }
}
