using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThgOptimization.Core.Benchmark;
using ThgOptimization.Views;

namespace ThgOptimization.Tests;

/// <summary>Página e gráfico montados do XAML real, com uma sessão de verdade. Com THG_RENDER_DIR definida, grava um PNG para conferir o layout a olho.</summary>
[Collection(WpfCollection.Name)]
public class HistoryPageRenderTests
{
    private static SessionTimeline Session()
    {
        var samples = new List<SessionSample>();
        var random = new Random(7);
        for (int i = 0; i < 600; i++)
        {
            bool spike = i % 97 == 0;
            samples.Add(new SessionSample(i * 1000L, spike ? 80 + random.NextDouble() * 40 : 7 + random.NextDouble() * 3, 130 + random.NextDouble() * 20,
                55 + i / 30.0, 40, 5200 - i / 4.0, 90, 62 + i / 40.0, 95, 2600 - i / 5.0, 240, 60, 45, null, i is > 400 and < 410, spike ? 1 : 0, i == 300));
        }
        return new SessionTimeline(new SessionHeader(1, "Counter-Strike 2", new DateTime(2026, 11, 3, 20, 0, 0), 600, 140, 92, 7), samples);
    }

    [Fact]
    public void Chart_draws_every_track_with_markers_and_cursor()
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/tHgOptimization;component/") };
            var win = (Window)XamlReader.Parse(XamlSmokeTests.Loose(File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml"))), context);
            var page = (Grid)win.FindName("PageHistory");
            ((Panel)page.Parent).Children.Remove(page);
            page.Margin = new Thickness(36, 32, 36, 32);
            page.Visibility = Visibility.Visible;

            var session = Session();
            var chart = (SessionTimelineChart)win.FindName("HistoryChart");
            chart.Show(session);
            ((ItemsControl)win.FindName("HistoryWorst")).ItemsSource = session.WorstFrames().Select(f => f.Line).ToList();
            ((TextBlock)win.FindName("HistoryPercentiles")).Text = "p50 8,5 ms · p95 9,9 ms · p99 10 ms · p99,9 120 ms";
            ((ComboBox)win.FindName("HistoryGame")).ItemsSource = new[] { "Todos os jogos", "Counter-Strike 2" };
            ((ComboBox)win.FindName("HistoryGame")).SelectedIndex = 0;
            ((ListBox)win.FindName("HistoryList")).ItemsSource = new[] { new { Game = "Counter-Strike 2", session.Header.Line } };

            var host = new Border { Background = (Brush)Application.Current.FindResource("Bg"), Child = page };
            host.Measure(new Size(1060, 760));
            host.Arrange(new Rect(0, 0, 1060, 760));
            host.UpdateLayout();

            Assert.True(chart.ActualHeight > 120, $"O gráfico ficou com {chart.ActualHeight} px.");
            var bitmap = new RenderTargetBitmap(1060, 760, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host); // OnRender roda de verdade aqui: recurso de cor faltando ou conta errada quebraria
            if (Environment.GetEnvironmentVariable("THG_RENDER_DIR") is { Length: > 0 } dir)
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(dir, "historico.png"));
                png.Save(file);
            }
        });
    }

    [Fact]
    public void Chart_without_a_session_shows_the_hint()
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            var chart = new SessionTimelineChart();
            chart.Show(null);
            chart.Measure(new Size(400, 200));
            chart.Arrange(new Rect(0, 0, 400, 200));
            var bitmap = new RenderTargetBitmap(400, 200, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);
            chart.Show(new SessionTimeline(new SessionHeader(1, "x", DateTime.Now, 0, 0, 0, 0), Array.Empty<SessionSample>()));
            bitmap.Render(chart);
        });
    }
}
