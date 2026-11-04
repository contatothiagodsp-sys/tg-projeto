using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Views;

/// <summary>
/// Três faixas no mesmo eixo de tempo: frametime (0 ms no topo, picos para baixo), temperaturas e clocks. Linhas verticais marcam engasgos, temperatura alta e erros de hardware.
/// Desenhado de uma vez em OnRender: milhares de pontos sem um elemento por ponto, e só redesenha ao mudar a sessão, o zoom ou o cursor.
/// Roda do mouse dá zoom, arrastar move, e o botão direito tem + , − e Reset.
/// </summary>
public sealed class SessionTimelineChart : FrameworkElement
{
    private const double LeftAxis = 80, TopGap = 6, TrackGap = 14, BottomAxis = 20;

    private SessionTimeline? _session;
    private TimelineViewport? _view;
    private double? _cursorX;
    private double? _dragFrom;

    public SessionTimelineChart()
    {
        ClipToBounds = true;
        Focusable = true;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Aproximar (+)", () => ZoomBy(0.5)));
        menu.Items.Add(MenuItem("Afastar (−)", () => ZoomBy(2)));
        menu.Items.Add(MenuItem("Reset", ResetView));
        ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    public void Show(SessionTimeline? session)
    {
        _session = session;
        _view = session != null ? new TimelineViewport(session.DurationMs) : null;
        _cursorX = null;
        InvalidateVisual();
    }

    private void ZoomBy(double factor)
    {
        _view?.Zoom(0.5, factor);
        InvalidateVisual();
    }

    private void ResetView()
    {
        _view?.Reset();
        InvalidateVisual();
    }

    private double PlotWidth => Math.Max(ActualWidth - LeftAxis, 1);
    private double Fraction(double x) => (x - LeftAxis) / PlotWidth;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_view == null) return;
        _view.Zoom(Fraction(e.GetPosition(this).X), e.Delta > 0 ? 0.7 : 1 / 0.7);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragFrom = e.GetPosition(this).X;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_view == null) return;
        double x = e.GetPosition(this).X;
        if (_dragFrom is double from)
        {
            _view.Pan((from - x) / PlotWidth);
            _dragFrom = x;
        }
        _cursorX = x;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _cursorX = null;
        InvalidateVisual();
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private FormattedText Text(string text, Brush brush, double size = 10) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // recebe o mouse em toda a área
        if (_session == null || _view == null || _session.Samples.Count == 0)
        {
            dc.DrawText(Text("Escolha uma sessão na lista.", Res("Muted"), 13), new Point(LeftAxis, 20));
            return;
        }
        double trackHeight = Math.Max((ActualHeight - TopGap - BottomAxis - TrackGap * 2) / 3, 10);
        var samples = VisibleSamples();
        Rect Track(int i) => new(LeftAxis, TopGap + i * (trackHeight + TrackGap), PlotWidth, trackHeight);

        DrawTrack(dc, Track(0), samples, "Frametime (ms)", invert: true, floorAtZero: true, new[] { (Func<SessionSample, double?>)(s => s.FrametimeMs) }, new[] { Res("Accent") }, spikes: Res("Bad"));
        DrawTrack(dc, Track(1), samples, "Temp. (°C)", invert: false, floorAtZero: false, new Func<SessionSample, double?>[] { s => s.CpuTemp, s => s.GpuTemp }, new[] { Res("Warn"), Res("Accent") }, spikes: null);
        DrawTrack(dc, Track(2), samples, "Clock (MHz)", invert: false, floorAtZero: false, new Func<SessionSample, double?>[] { s => s.CpuClock, s => s.GpuClock }, new[] { Res("Warn"), Res("Accent") }, spikes: null);
        DrawMarkers(dc, samples, trackHeight);
        DrawTimeAxis(dc);
        DrawCursor(dc);
    }

    private List<SessionSample> VisibleSamples()
    {
        var all = _session!.Samples;
        var list = new List<SessionSample>();
        foreach (var s in all)
            if (s.TimestampMs >= _view!.StartMs - 1000 && s.TimestampMs <= _view.EndMs + 1000) list.Add(s);
        return list;
    }

    private double XOf(long ms) => LeftAxis + _view!.FractionOf(ms) * PlotWidth;

    private void DrawTrack(DrawingContext dc, Rect area, List<SessionSample> samples, string title, bool invert, bool floorAtZero,
                           Func<SessionSample, double?>[] series, Brush[] colors, Brush? spikes)
    {
        var grid = new Pen(Res("Line"), 1);
        grid.Freeze();
        dc.DrawLine(grid, area.TopLeft, area.TopRight);
        dc.DrawLine(grid, area.BottomLeft, area.BottomRight);

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var s in samples)
            foreach (var read in series)
                if (read(s) is double v)
                {
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
        dc.DrawText(Text(title, Res("Faint")), new Point(4, area.Top));
        if (lo > hi)
        {
            dc.DrawText(Text("sem leitura", Res("Faint")), new Point(area.Left + 6, area.Top + 4));
            return;
        }
        if (floorAtZero) lo = 0;
        if (hi - lo < 1) hi = lo + 1;
        dc.DrawText(Text(hi.ToString("0"), Res("Faint")), new Point(4, invert ? area.Bottom - 12 : area.Top + 12));
        dc.DrawText(Text(lo.ToString("0"), Res("Faint")), new Point(4, invert ? area.Top + 12 : area.Bottom - 12));

        double YOf(double v)
        {
            double t = (v - lo) / (hi - lo);
            return invert ? area.Top + t * area.Height : area.Bottom - t * area.Height;
        }

        for (int i = 0; i < series.Length; i++)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool open = false;
                foreach (var s in samples)
                {
                    if (series[i](s) is not double v) { open = false; continue; } // sem leitura: a linha quebra, não inventa ponte
                    var p = new Point(XOf(s.TimestampMs), YOf(v));
                    if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                    else ctx.LineTo(p, true, false);
                }
            }
            geometry.Freeze();
            var pen = new Pen(colors[i], 1.2);
            pen.Freeze();
            dc.PushClip(new RectangleGeometry(area));
            dc.DrawGeometry(null, pen, geometry);
            dc.Pop();
        }

        if (spikes != null)
        {
            var pen = new Pen(spikes, 1.4);
            pen.Freeze();
            foreach (var s in samples)
                if (s.StuttersThisSecond > 0 && s.FrametimeMs is double ms)
                    dc.DrawLine(pen, new Point(XOf(s.TimestampMs), area.Top), new Point(XOf(s.TimestampMs), YOf(ms)));
        }
    }

    private void DrawMarkers(DrawingContext dc, List<SessionSample> samples, double trackHeight)
    {
        var dash = new Pen(Res("Bad"), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        dash.Freeze();
        double top = TopGap, bottom = TopGap + trackHeight * 3 + TrackGap * 2;
        foreach (var s in samples)
        {
            double x = XOf(s.TimestampMs);
            if (x < LeftAxis || x > ActualWidth) continue;
            if (s.StuttersThisSecond > 0) dc.DrawLine(dash, new Point(x, top), new Point(x, bottom));
            if (s.Throttled) dc.DrawText(Text("↑", Res("Warn"), 11), new Point(x - 3, top + trackHeight + TrackGap));
            if (s.WheaError) dc.DrawText(Text("!", Res("Bad"), 13), new Point(x - 2, top));
        }
    }

    private void DrawTimeAxis(DrawingContext dc)
    {
        double y = ActualHeight - BottomAxis + 4;
        const int Ticks = 6;
        for (int i = 0; i <= Ticks; i++)
        {
            long ms = _view!.MsAt(i / (double)Ticks);
            var text = Text(TimeSpan.FromMilliseconds(ms).ToString(@"h\:mm\:ss"), Res("Faint"));
            double x = LeftAxis + i / (double)Ticks * PlotWidth - (i == Ticks ? text.Width : i == 0 ? 0 : text.Width / 2);
            dc.DrawText(text, new Point(x, y));
        }
    }

    /// <summary>Balão com os valores do segundo sob o cursor.</summary>
    private void DrawCursor(DrawingContext dc)
    {
        if (_cursorX is not double x || x < LeftAxis) return;
        var pen = new Pen(Res("Muted"), 1);
        pen.Freeze();
        dc.DrawLine(pen, new Point(x, TopGap), new Point(x, ActualHeight - BottomAxis));
        long ms = _view!.MsAt(Fraction(x));
        if (_session!.SampleAt(ms) is not { } s) return;
        var text = Text($"{TimeSpan.FromMilliseconds(ms):h\\:mm\\:ss}  ·  {N(s.Fps, "FPS")}  ·  {N(s.FrametimeMs, "ms", "0.0")}\n" +
                        $"CPU {N(s.CpuTemp, "°C")} {N(s.CpuClock, "MHz")} {N(s.CpuLoad, "%")}\nGPU {N(s.GpuTemp, "°C")} {N(s.GpuClock, "MHz")} {N(s.GpuLoad, "%")}\n" +
                        $"VRAM {N(s.VramPct, "%")}  ·  RAM {N(s.RamPct, "%")}" + (s.StuttersThisSecond > 0 ? $"\n{s.StuttersThisSecond} engasgo(s)" : "") + (s.WheaError ? "\nErro de hardware (WHEA)" : ""),
            Res("Fg"), 11);
        double boxX = x + 10 + text.Width + 12 > ActualWidth ? x - text.Width - 22 : x + 10;
        var box = new Rect(boxX, TopGap + 2, text.Width + 12, text.Height + 8);
        dc.DrawRoundedRectangle(Res("Bg1Brush"), new Pen(Res("Line"), 1), box, 4, 4);
        dc.DrawText(text, new Point(box.X + 6, box.Y + 4));
    }

    private static string N(double? v, string unit, string format = "0") => v is double d ? $"{d.ToString(format)} {unit}" : "—";
}
