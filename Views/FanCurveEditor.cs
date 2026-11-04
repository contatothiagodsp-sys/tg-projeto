using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ThgOptimization.Views;

/// <summary>A curva como dados: pontos sempre em ordem de temperatura, nos limites e nunca menos de dois. O texto é o mesmo formato que FanCurves.ParseCustom lê.</summary>
public sealed class FanCurveModel
{
    public const double MaxTemp = 110, MaxPercent = 100, MinGap = 1;
    public const int MinPoints = 2;
    public const string DefaultText = "30:30, 50:40, 65:60, 75:80, 85:100";

    private readonly List<(double T, double P)> _points = new();

    public IReadOnlyList<(double T, double P)> Points => _points;

    public FanCurveModel(IEnumerable<(double T, double P)>? points = null) => Load(points ?? FanCurves.ParseCustom(DefaultText));

    /// <summary>Só mostra a curva: quem decide gravar é o dono do editor, no fim de uma edição do usuário.</summary>
    public void Load(IEnumerable<(double T, double P)> points)
    {
        _points.Clear();
        _points.AddRange(points.OrderBy(p => p.T));
    }

    public void Load(string text) => Load(FanCurves.ParseCustom(text));

    /// <summary>Devolve o índice do ponto novo, ou -1 se cair em cima de outro.</summary>
    public int Add(double t, double p)
    {
        t = Math.Clamp(Math.Round(t), 0, MaxTemp);
        p = Math.Clamp(Math.Round(p), 0, MaxPercent);
        if (_points.Any(x => Math.Abs(x.T - t) < MinGap)) return -1;
        int index = _points.FindIndex(x => x.T > t);
        if (index < 0) index = _points.Count;
        _points.Insert(index, (t, p));
        return index;
    }

    public bool Remove(int index)
    {
        if (_points.Count <= MinPoints || index < 0 || index >= _points.Count) return false;
        _points.RemoveAt(index);
        return true;
    }

    /// <summary>Move sem atravessar os vizinhos: a ordem dos pontos não muda ao arrastar.</summary>
    public void Move(int index, double t, double p)
    {
        if (index < 0 || index >= _points.Count) return;
        double lo = index > 0 ? _points[index - 1].T + MinGap : 0;
        double hi = index < _points.Count - 1 ? _points[index + 1].T - MinGap : MaxTemp;
        _points[index] = (Math.Clamp(Math.Round(t), lo, Math.Max(lo, hi)), Math.Clamp(Math.Round(p), 0, MaxPercent));
    }

    public string ToText() => string.Join(", ", _points.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.T:0.#}:{x.P:0.#}")));
}

/// <summary>
/// Editor da curva: arraste os pontos, clique na área vazia para adicionar e use o botão direito para remover. Grava só quando o usuário termina de editar,
/// no formato de texto de sempre, que continua disponível em "Editar como texto".
/// </summary>
public sealed class FanCurveEditor : UserControl
{
    private const double PlotWidth = 420, PlotHeight = 200, Dot = 12;

    private readonly FanCurveModel _model = new();
    private readonly Canvas _canvas = new() { Width = PlotWidth, Height = PlotHeight, ClipToBounds = false, Background = Brushes.Transparent };
    private readonly Polyline _line = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Line _tempLine = new() { StrokeThickness = 1, Y1 = 0, Y2 = PlotHeight, Visibility = Visibility.Collapsed, IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 3, 3 } };
    private readonly TextBlock _live = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _text = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<Ellipse> _dots = new();
    private readonly Button _test;
    private int _dragging = -1;

    /// <summary>O usuário terminou uma edição: o texto é o que vai para as configurações.</summary>
    public event Action<string>? CurveEdited;
    /// <summary>TESTAR 10 s, com a curva como está na tela.</summary>
    public event Action<string>? TestRequested;

    public FanCurveEditor()
    {
        _canvas.SetResourceReference(BackgroundProperty, "Bg0Brush");
        _line.SetResourceReference(Shape.StrokeProperty, "Accent");
        _tempLine.SetResourceReference(Shape.StrokeProperty, "Warn");
        _live.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DrawGrid();
        _canvas.Children.Add(_tempLine);
        _canvas.Children.Add(_line);
        _canvas.MouseLeftButtonDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp += Canvas_MouseUp;
        AutomationProperties_SetName(_canvas);

        var reset = new Button { Content = "RESTAURAR PADRÃO", Padding = new Thickness(12, 4, 12, 4), FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
        reset.SetResourceReference(StyleProperty, "OutlineButton");
        reset.Click += (_, _) => { _model.Load(FanCurveModel.DefaultText); Redraw(); Commit(); };
        _test = new Button { Content = "TESTAR 10 s", Padding = new Thickness(12, 4, 12, 4), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Aplica esta curva por 10 segundos e volta ao que estava" };
        _test.SetResourceReference(StyleProperty, "OutlineButton");
        _test.Click += (_, _) => TestRequested?.Invoke(_model.ToText());

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        DockPanel.SetDock(_test, Dock.Right);
        DockPanel.SetDock(reset, Dock.Right);
        footer.Children.Add(_test);
        footer.Children.Add(reset);
        footer.Children.Add(_live);

        _text.LostFocus += (_, _) =>
        {
            if (_text.Text == _model.ToText()) return;
            _model.Load(_text.Text);
            Redraw();
            Commit();
        };
        var advanced = new Expander { Header = "Editar como texto (°C:% separados por vírgula)", Content = _text, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        advanced.SetResourceReference(StyleProperty, "ThgExpander");

        var root = new StackPanel();
        root.Children.Add(new Border { Child = _canvas, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(28, 4, 8, 20) });
        root.Children.Add(footer);
        root.Children.Add(advanced);
        Content = root;
        Redraw();
    }

    private static void AutomationProperties_SetName(UIElement element) =>
        System.Windows.Automation.AutomationProperties.SetName(element, "Curva da fan: temperatura no eixo horizontal e rotação no vertical");

    private static double X(double t) => t / FanCurveModel.MaxTemp * PlotWidth;
    private static double Y(double p) => PlotHeight - p / FanCurveModel.MaxPercent * PlotHeight;
    private static double TempAt(double x) => x / PlotWidth * FanCurveModel.MaxTemp;
    private static double PercentAt(double y) => (PlotHeight - y) / PlotHeight * FanCurveModel.MaxPercent;

    private void DrawGrid()
    {
        for (int t = 0; t <= 100; t += 20) AddGridLine(X(t), 0, X(t), PlotHeight, $"{t}°", X(t) - 8, PlotHeight + 3);
        for (int p = 0; p <= 100; p += 20) AddGridLine(0, Y(p), PlotWidth, Y(p), $"{p}%", -28, Y(p) - 7);
    }

    private void AddGridLine(double x1, double y1, double x2, double y2, string label, double labelX, double labelY)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1, IsHitTestVisible = false };
        line.SetResourceReference(Shape.StrokeProperty, "Line");
        _canvas.Children.Add(line);
        var text = new TextBlock { Text = label, FontSize = 10, IsHitTestVisible = false };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        Canvas.SetLeft(text, labelX);
        Canvas.SetTop(text, labelY);
        _canvas.Children.Add(text);
    }

    /// <summary>Mostra uma curva sem marcá-la como editada: abrir a página ou trocar de preset não muda nada nas configurações.</summary>
    public void Load(IEnumerable<(double T, double P)> points)
    {
        if (_dragging >= 0) return;
        _model.Load(points);
        Redraw();
    }

    /// <summary>Linha da temperatura atual e o valor ao vivo. Null = sem leitura.</summary>
    public void SetLive(double? temperature, string text)
    {
        if (_live.Text != text) _live.Text = text;
        if (temperature is double t)
        {
            _tempLine.X1 = _tempLine.X2 = X(Math.Clamp(t, 0, FanCurveModel.MaxTemp));
            _tempLine.Visibility = Visibility.Visible;
        }
        else _tempLine.Visibility = Visibility.Collapsed;
    }

    public void SetTesting(bool testing)
    {
        _test.IsEnabled = !testing;
        _test.Content = testing ? "TESTANDO…" : "TESTAR 10 s";
    }

    private void Redraw()
    {
        foreach (var dot in _dots) _canvas.Children.Remove(dot);
        _dots.Clear();
        var points = new PointCollection(_model.Points.Count);
        foreach (var (t, p) in _model.Points) points.Add(new Point(X(t), Y(p)));
        _line.Points = points;
        for (int i = 0; i < _model.Points.Count; i++) AddDot(i);
        _text.Text = _model.ToText();
    }

    private void AddDot(int index)
    {
        var dot = new Ellipse { Width = Dot, Height = Dot, Cursor = Cursors.SizeAll, Tag = index };
        dot.SetResourceReference(Shape.FillProperty, "Accent");
        Place(dot, index);
        dot.MouseLeftButtonDown += Dot_MouseDown;
        dot.MouseRightButtonUp += Dot_RightClick;
        _dots.Add(dot);
        _canvas.Children.Add(dot);
    }

    private void Place(Ellipse dot, int index)
    {
        var (t, p) = _model.Points[index];
        Canvas.SetLeft(dot, X(t) - Dot / 2);
        Canvas.SetTop(dot, Y(p) - Dot / 2);
        dot.ToolTip = string.Create(CultureInfo.CurrentCulture, $"{t:0} °C · {p:0} %");
    }

    private void Dot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = (int)((FrameworkElement)sender).Tag;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Dot_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (!_model.Remove((int)((FrameworkElement)sender).Tag)) return;
        e.Handled = true;
        Redraw();
        Commit();
    }

    /// <summary>Clique na área vazia: ponto novo, já pronto para arrastar.</summary>
    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(_canvas);
        int index = _model.Add(TempAt(at.X), PercentAt(at.Y));
        if (index < 0) return;
        Redraw();
        _dragging = index;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging < 0) return;
        var at = e.GetPosition(_canvas);
        _model.Move(_dragging, TempAt(at.X), PercentAt(at.Y));
        // Só o ponto arrastado e a linha: recriar os pontos soltaria o que está sendo arrastado.
        Place(_dots[_dragging], _dragging);
        var (t, p) = _model.Points[_dragging];
        var line = _line.Points.Clone();
        line[_dragging] = new Point(X(t), Y(p));
        _line.Points = line;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging < 0) return;
        _dragging = -1;
        _canvas.ReleaseMouseCapture();
        _text.Text = _model.ToText();
        Commit();
    }

    private void Commit() => CurveEdited?.Invoke(_model.ToText());
}
