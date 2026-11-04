using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ThgOptimization.Views;

/// <summary>
/// Gráfico de área do painel (CPU × GPU). Redesenhado a cada segundo: os elementos são criados uma vez, na ordem de desenho, e cada
/// atualização só troca propriedades. As cores são lidas de novo a cada vez, para acompanhar a troca de tema.
/// </summary>
internal sealed class DashChartView
{
    private const double Left = 48, Bottom = 22, Top = 8, MinWidth = 80, MinHeight = 60;
    private const int GridLines = 3, TimeLabels = 3;

    private static readonly DoubleCollection GridDash = FrozenDash(5, 5), GpuDash = FrozenDash(3, 2), MarkerDash = FrozenDash(3, 3);

    private readonly Func<string, object> _resource;
    private readonly Line[] _gridLines = new Line[GridLines];
    private readonly TextBlock[] _gridLabels = new TextBlock[GridLines];
    private readonly TextBlock[] _timeLabels = new TextBlock[TimeLabels];
    private readonly Polyline _gpuLine = new() { StrokeThickness = 1.5, StrokeDashArray = GpuDash, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polygon _area = new();
    private readonly Polyline _cpuLine = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Line _marker = new() { StrokeDashArray = MarkerDash };
    private readonly Ellipse _dot = new() { Width = 11, Height = 11, StrokeThickness = 2 };
    private readonly TextBlock _bubbleText = new() { FontWeight = FontWeights.SemiBold };
    private readonly Border _bubble;
    private readonly UIElement[] _all;
    private Brush? _areaFill;
    private Color _areaColor;

    public DashChartView(Canvas canvas, Func<string, object> resource)
    {
        _resource = resource;
        _bubble = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5), Child = _bubbleText };
        for (int i = 0; i < GridLines; i++)
        {
            _gridLines[i] = new Line { StrokeDashArray = GridDash };
            _gridLabels[i] = new TextBlock { FontSize = 11 };
        }
        for (int i = 0; i < TimeLabels; i++) _timeLabels[i] = new TextBlock { FontSize = 11 };

        var parts = new List<UIElement>();
        for (int i = 0; i < GridLines; i++) { parts.Add(_gridLines[i]); parts.Add(_gridLabels[i]); }
        parts.AddRange(_timeLabels);
        parts.AddRange(new UIElement[] { _gpuLine, _area, _cpuLine, _marker, _dot, _bubble });
        _all = parts.ToArray();
        foreach (var part in _all) canvas.Children.Add(part);
        SetVisible(false, _all);
    }

    /// <param name="range">Tamanho da janela de tempo, em amostras (o eixo X). A série encosta à direita.</param>
    /// <param name="cpu">Valores da CPU; null = sem leitura naquela amostra. Mesmo tamanho de <paramref name="gpu"/>.</param>
    public void Draw(double width, double height, int range, string unit, double yMin, double yMax, IReadOnlyList<float?> cpu, IReadOnlyList<float?> gpu, string valueText)
    {
        if (width < MinWidth || height < MinHeight)
        {
            SetVisible(false, _all);
            return;
        }
        double plotW = width - Left, plotH = height - Top - Bottom;
        int count = cpu.Count, span = Math.Max(1, range - 1);
        double X(int i) => Left + plotW * i / span + plotW * (range - count) / span;
        double Y(double v) => Top + plotH * (1 - (Math.Clamp(v, yMin, yMax) - yMin) / (yMax - yMin));

        var fg = (SolidColorBrush)_resource("Fg");
        var faint = (Brush)_resource("Faint");
        var lineBrush = (Brush)_resource("Line");
        var mono = (FontFamily)_resource("Mono");

        // Grade tracejada horizontal e rótulos
        for (int k = 0; k < GridLines; k++)
        {
            double v = yMin + (yMax - yMin) * k / (GridLines - 1), y = Y(v);
            var line = _gridLines[k];
            line.X1 = Left; line.X2 = width; line.Y1 = y; line.Y2 = y; line.Stroke = lineBrush;
            var label = _gridLabels[k];
            label.Text = $"{v:0}{unit.Trim()}";
            label.Foreground = faint;
            label.FontFamily = mono;
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 8);
        }
        // Eixo de tempo
        string[] times = { range >= 60 ? $"-{range / 60} min" : $"-{range} s", range >= 120 ? $"-{range / 120} min" : $"-{range / 2} s", "agora" };
        for (int k = 0; k < TimeLabels; k++)
        {
            var label = _timeLabels[k];
            label.Text = times[k];
            label.Foreground = faint;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(Left + plotW * k / (TimeLabels - 1) - label.DesiredSize.Width / 2, Left, width - label.DesiredSize.Width));
            Canvas.SetTop(label, height - 16);
        }
        SetVisible(true, _gridLines);
        SetVisible(true, _gridLabels);
        SetVisible(true, _timeLabels);

        var cpuPoints = new PointCollection(count + 2);
        var gpuPoints = new PointCollection(count);
        for (int i = 0; i < count; i++)
        {
            if (cpu[i] is float c) cpuPoints.Add(new Point(X(i), Y(c)));
            if (gpu[i] is float g) gpuPoints.Add(new Point(X(i), Y(g)));
        }

        // GPU: linha tracejada discreta
        bool anyData = count >= 2;
        if (anyData)
        {
            gpuPoints.Freeze();
            _gpuLine.Points = gpuPoints;
            _gpuLine.Stroke = (Brush)_resource("Muted");
        }
        SetVisible(anyData, _gpuLine);

        // CPU: área com degradê monocromático, linha e marcador do valor atual
        bool drawCpu = anyData && cpuPoints.Count >= 2;
        SetVisible(drawCpu, _area, _cpuLine, _marker, _dot, _bubble);
        if (!drawCpu) return;

        Point first = cpuPoints[0], last = cpuPoints[^1];
        var linePoints = cpuPoints.Clone();
        linePoints.Freeze();
        _cpuLine.Points = linePoints;
        _cpuLine.Stroke = fg;
        cpuPoints.Add(new Point(last.X, Top + plotH));
        cpuPoints.Add(new Point(first.X, Top + plotH));
        cpuPoints.Freeze();
        _area.Points = cpuPoints;
        _area.Fill = AreaFill(fg.Color);

        _marker.X1 = last.X; _marker.X2 = last.X; _marker.Y1 = Top; _marker.Y2 = Top + plotH; _marker.Stroke = faint;
        _dot.Stroke = fg;
        _dot.Fill = (Brush)_resource("Card");
        Canvas.SetLeft(_dot, last.X - 5.5);
        Canvas.SetTop(_dot, last.Y - 5.5);
        _bubble.Background = (Brush)_resource("Hover");
        _bubbleText.Text = valueText;
        _bubbleText.FontFamily = mono;
        _bubbleText.Foreground = fg;
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_bubble, Math.Max(Left, last.X - _bubble.DesiredSize.Width - 10));
        Canvas.SetTop(_bubble, Math.Clamp(last.Y - _bubble.DesiredSize.Height - 10, 0, height - _bubble.DesiredSize.Height));
    }

    /// <summary>Só muda com o tema: o mesmo degradê serve para todos os quadros.</summary>
    private Brush AreaFill(Color color)
    {
        if (_areaFill != null && _areaColor == color) return _areaFill;
        _areaColor = color;
        _areaFill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B), 90).Frozen();
        return _areaFill;
    }

    private static void SetVisible(bool visible, params UIElement[] parts)
    {
        var state = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var part in parts) part.Visibility = state;
    }

    internal static DoubleCollection FrozenDash(double on, double off) => new DoubleCollection { on, off }.Frozen();
}

/// <summary>Anéis concêntricos do painel. Mesma ideia do gráfico: elementos fixos, e o arco só é refeito quando o valor muda de forma visível.</summary>
internal sealed class DashRingsView
{
    internal const int MaxRings = 3;
    private const double StrokeWidth = 12, ArcEpsilon = 0.0005;

    private sealed record Slot(Ellipse Track, Path Arc)
    {
        public double DrawnFraction { get; set; } = double.NaN;
    }

    private readonly Slot[] _slots = new Slot[MaxRings];
    private readonly TextBlock _center = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly double _cx, _cy;

    public DashRingsView(Canvas canvas)
    {
        _cx = canvas.Width / 2;
        _cy = canvas.Height / 2;
        for (int i = 0; i < MaxRings; i++)
        {
            double r = Radius(i);
            var track = new Ellipse { Width = r * 2, Height = r * 2, StrokeThickness = StrokeWidth, Margin = new Thickness(_cx - r, _cy - r, 0, 0), Visibility = Visibility.Collapsed };
            var arc = new Path { StrokeThickness = StrokeWidth, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed };
            _slots[i] = new Slot(track, arc);
            canvas.Children.Add(track);
            canvas.Children.Add(arc);
        }
        canvas.Children.Add(_center);
    }

    private static double Radius(int ring) => 86 - ring * 22;

    /// <param name="rings">Do anel de fora para o de dentro, no máximo <see cref="MaxRings"/>; o que passar disso não é desenhado.</param>
    public void Draw(IReadOnlyList<(double Fraction, Brush Brush)> rings, string centerText, Brush trackBrush, Brush textBrush, FontFamily mono)
    {
        for (int i = 0; i < MaxRings; i++)
        {
            var slot = _slots[i];
            bool used = i < rings.Count;
            slot.Track.Visibility = slot.Arc.Visibility = used ? Visibility.Visible : Visibility.Collapsed;
            if (!used) continue;
            var (fraction, brush) = rings[i];
            slot.Track.Stroke = trackBrush;
            slot.Arc.Stroke = brush;
            if (double.IsNaN(slot.DrawnFraction) || Math.Abs(slot.DrawnFraction - fraction) >= ArcEpsilon)
            {
                slot.Arc.Data = Arc(fraction, _cx, _cy, Radius(i));
                slot.DrawnFraction = fraction;
            }
        }
        _center.Text = centerText;
        _center.FontFamily = mono;
        _center.Foreground = textBrush;
        _center.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_center, _cx - _center.DesiredSize.Width / 2);
        Canvas.SetTop(_center, _cy - _center.DesiredSize.Height / 2);
    }

    private static Geometry Arc(double fraction, double cx, double cy, double r)
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        if (fraction <= 0.001) return Geometry.Empty;
        double a = fraction * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Point(cx, cy - r) };
        figure.Segments.Add(new ArcSegment(new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)), new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { figure }).Frozen();
    }
}
