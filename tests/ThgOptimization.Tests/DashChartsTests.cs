using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ThgOptimization.Views;

namespace ThgOptimization.Tests;

/// <summary>O painel redesenha a cada segundo: os elementos ficam e só as propriedades mudam.</summary>
public class DashChartsTests
{
    private static object Resource(string key) => key switch
    {
        "Fg" => Brushes.White,
        "Mono" => new FontFamily("Consolas"),
        _ => Brushes.Gray,
    };

    private static float?[] Series(params float?[] values) => values;

    private static void Draw(DashChartView view, float?[] cpu, float?[] gpu, double width = 600, double height = 210) =>
        view.Draw(width, height, 300, " °C", 20, 90, cpu, gpu, "70 °C");

    private static Visibility[] States(Canvas canvas) => canvas.Children.OfType<UIElement>().Select(e => e.Visibility).ToArray();

    [Fact]
    public void Chart_keeps_the_same_elements_between_updates() => Sta.Run(() =>
    {
        var canvas = new Canvas();
        var view = new DashChartView(canvas, Resource);
        Draw(view, Series(50, 60, 70), Series(40, 41, 42));
        var first = canvas.Children.OfType<UIElement>().ToList();

        Draw(view, Series(50, 60, 70, 80), Series(40, 41, 42, 43));

        Assert.Equal(first, canvas.Children.OfType<UIElement>());
    });

    [Fact]
    public void Chart_updates_the_points_and_the_bubble() => Sta.Run(() =>
    {
        var canvas = new Canvas();
        var view = new DashChartView(canvas, Resource);
        Draw(view, Series(50, 60, 70), Series(40, 41, 42));
        var lines = canvas.Children.OfType<Polyline>().ToList();
        var gpuLine = lines[0];
        var cpuLine = lines[1];
        Assert.Equal(3, cpuLine.Points.Count);
        double lastY = cpuLine.Points[^1].Y;

        Draw(view, Series(50, 60, 70, 21), Series(40, 41, 42, 43));

        Assert.Equal(4, cpuLine.Points.Count);
        Assert.Equal(4, gpuLine.Points.Count);
        Assert.True(cpuLine.Points[^1].Y > lastY); // 21 °C fica mais baixo no gráfico que 70 °C
        Assert.Equal(6, canvas.Children.OfType<Polygon>().Single().Points.Count); // os pontos da CPU mais os dois do fundo da área
        Assert.Equal("70 °C", ((TextBlock)canvas.Children.OfType<Border>().Single().Child).Text);
    });

    [Fact]
    public void Chart_keeps_the_drawing_order() => Sta.Run(() =>
    {
        var canvas = new Canvas();
        new DashChartView(canvas, Resource);
        var types = canvas.Children.OfType<UIElement>().Select(e => e.GetType()).ToList();
        Assert.Equal(typeof(Line), types[0]);
        Assert.True(types.IndexOf(typeof(Polyline)) < types.IndexOf(typeof(Polygon))); // GPU atrás da área da CPU
        Assert.True(types.IndexOf(typeof(Polygon)) < types.LastIndexOf(typeof(Polyline))); // e a linha da CPU na frente dela
        Assert.Equal(typeof(Border), types[^1]); // o balão por cima de tudo
    });

    [Fact]
    public void Chart_is_hidden_when_too_small_and_comes_back() => Sta.Run(() =>
    {
        var canvas = new Canvas();
        var view = new DashChartView(canvas, Resource);
        Draw(view, Series(50, 60), Series(40, 41), width: 50);
        Assert.All(States(canvas), s => Assert.Equal(Visibility.Collapsed, s));

        Draw(view, Series(50, 60), Series(40, 41));

        Assert.All(States(canvas), s => Assert.Equal(Visibility.Visible, s));
    });

    /// <summary>Antes de ter 2 amostras, ou sem leitura da CPU, o gráfico mostra só o que dá para desenhar.</summary>
    [Fact]
    public void Chart_shows_only_what_the_data_allows() => Sta.Run(() =>
    {
        var canvas = new Canvas();
        var view = new DashChartView(canvas, Resource);

        Draw(view, Series(50), Series(40));
        var lines = canvas.Children.OfType<Polyline>().ToList();
        Assert.Equal(Visibility.Collapsed, lines[0].Visibility);
        Assert.Equal(Visibility.Collapsed, lines[1].Visibility);
        Assert.Equal(Visibility.Collapsed, canvas.Children.OfType<Border>().Single().Visibility);
        Assert.Equal(Visibility.Visible, canvas.Children.OfType<Line>().First().Visibility); // a grade aparece logo

        Draw(view, Series(null, null), Series(40, 41));
        Assert.Equal(Visibility.Visible, lines[0].Visibility); // GPU com dados, CPU sem
        Assert.Equal(Visibility.Collapsed, lines[1].Visibility);
        Assert.Equal(Visibility.Collapsed, canvas.Children.OfType<Polygon>().Single().Visibility);

        // Uma sessão inteira sem leitura depois de ter tido: a curva velha não pode ficar na tela.
        Draw(view, Series(50, 60), Series(40, 41));
        Assert.Equal(Visibility.Visible, lines[1].Visibility);
        Draw(view, Series(null, null), Series(null, null));
        Assert.Equal(Visibility.Collapsed, lines[1].Visibility);
        Assert.Empty(lines[0].Points);
    });

    [Fact]
    public void Dashes_and_area_fill_are_frozen_and_shared() => Sta.Run(() =>
    {
        var canvas = new Canvas();
        var view = new DashChartView(canvas, Resource);
        Draw(view, Series(50, 60), Series(40, 41));
        var grid = canvas.Children.OfType<Line>().First();
        var area = canvas.Children.OfType<Polygon>().Single();
        Assert.True(grid.StrokeDashArray.IsFrozen);
        var fill = area.Fill;
        Assert.True(fill.IsFrozen);

        Draw(view, Series(50, 60, 70), Series(40, 41, 42));

        Assert.Same(fill, area.Fill);
        Assert.True(area.Points.IsFrozen);
    });

    [Fact]
    public void Theme_change_reaches_the_reused_elements() => Sta.Run(() =>
    {
        Brush fg = Brushes.White;
        var canvas = new Canvas();
        var view = new DashChartView(canvas, key => key == "Fg" ? fg : Resource(key));
        Draw(view, Series(50, 60), Series(40, 41));
        var area = canvas.Children.OfType<Polygon>().Single();
        var darkFill = area.Fill;

        fg = Brushes.Black;
        Draw(view, Series(50, 60), Series(40, 41));

        Assert.Same(Brushes.Black, canvas.Children.OfType<Polyline>().ElementAt(1).Stroke);
        Assert.NotSame(darkFill, area.Fill);
    });

    private static Canvas RingCanvas() => new() { Width = 190, Height = 190 };
    private static readonly FontFamily Mono = new("Consolas");

    [Fact]
    public void Rings_reuse_the_elements_and_rebuild_the_arc_only_when_it_moves() => Sta.Run(() =>
    {
        var canvas = RingCanvas();
        var view = new DashRingsView(canvas);
        view.Draw(new[] { (0.5, (Brush)Brushes.White) }, "50%", Brushes.Gray, Brushes.White, Mono);
        var elements = canvas.Children.OfType<UIElement>().ToList();
        var arc = canvas.Children.OfType<Path>().First();
        var data = arc.Data;

        view.Draw(new[] { (0.50001, (Brush)Brushes.White) }, "50%", Brushes.Gray, Brushes.White, Mono);
        Assert.Same(data, arc.Data);

        view.Draw(new[] { (0.75, (Brush)Brushes.White) }, "75%", Brushes.Gray, Brushes.White, Mono);
        Assert.NotSame(data, arc.Data);
        Assert.True(arc.Data.IsFrozen);
        Assert.Equal(elements, canvas.Children.OfType<UIElement>());
        Assert.Equal("75%", canvas.Children.OfType<TextBlock>().Single().Text);
    });

    [Fact]
    public void Rings_show_only_the_ones_in_use() => Sta.Run(() =>
    {
        var canvas = RingCanvas();
        var view = new DashRingsView(canvas);
        var three = new[] { (0.5, (Brush)Brushes.White), (0.4, (Brush)Brushes.Gray), (0.3, (Brush)Brushes.Black) };

        view.Draw(three, "50%", Brushes.Gray, Brushes.White, Mono);
        Assert.All(canvas.Children.OfType<Path>(), p => Assert.Equal(Visibility.Visible, p.Visibility));

        view.Draw(three[..1], "50%", Brushes.Gray, Brushes.White, Mono);
        Assert.Equal(new[] { Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed }, canvas.Children.OfType<Path>().Select(p => p.Visibility));
        Assert.Equal(new[] { Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed }, canvas.Children.OfType<Ellipse>().Select(p => p.Visibility));
    });

    /// <summary>Quase vazio não desenha um pontinho: a geometria vazia precisa valer também em cima de um arco já desenhado.</summary>
    [Fact]
    public void Ring_near_zero_clears_the_arc() => Sta.Run(() =>
    {
        var canvas = RingCanvas();
        var view = new DashRingsView(canvas);
        view.Draw(new[] { (0.5, (Brush)Brushes.White) }, "50%", Brushes.Gray, Brushes.White, Mono);
        view.Draw(new[] { (0.0, (Brush)Brushes.White) }, "0%", Brushes.Gray, Brushes.White, Mono);
        Assert.Same(Geometry.Empty, canvas.Children.OfType<Path>().First().Data);
    });

    [Fact]
    public void Compare_chart_redraw_uses_frozen_shared_dashes()
    {
        var text = System.IO.File.ReadAllText(RepoFile(@"Views\FrametimeCompareChart.xaml.cs"));
        Assert.DoesNotContain("new DoubleCollection {", text.Replace("var dash = new DoubleCollection { on, off };", ""));
    }

    private static string RepoFile(string name)
    {
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "tHgOptimization.csproj"))) return System.IO.Path.Combine(d.FullName, name);
        throw new System.IO.FileNotFoundException(name);
    }
}
