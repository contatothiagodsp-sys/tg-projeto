using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ThgOptimization;

/// <summary>
/// OSD sobre o jogo (estilo RTSS/Afterburner): janela transparente, sempre no topo, que ignora cliques
/// e nunca rouba o foco. Aparece sobre jogos em tela cheia "sem bordas" e na maioria dos DX11/12 em
/// tela cheia (que o Windows apresenta via flip); não aparece em tela cheia exclusiva legada.
/// </summary>
public partial class OverlayWindow : Window
{
    public string Corner { get; set; } = "TL"; // TL, TR, BL, BR

    private IntPtr _hwnd, _hook;
    private IntPtr _monitor;                  // monitor do jogo (o da última janela ativa de outro programa)
    private readonly WinEventProc _onForeground; // guardado num campo para o GC não coletar o callback

    /// <summary>Linhas de sensores reaproveitadas: cada atualização só troca o texto, sem recriar os itens da lista.</summary>
    private sealed class RowView : System.ComponentModel.INotifyPropertyChanged
    {
        private static readonly System.ComponentModel.PropertyChangedEventArgs LabelArgs = new(nameof(Label)), ValueArgs = new(nameof(Value));
        private string _label = "", _value = "";
        public string Label { get => _label; set { if (_label != value) { _label = value; PropertyChanged?.Invoke(this, LabelArgs); } } }
        public string Value { get => _value; set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, ValueArgs); } } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<RowView> _rows = new();
    private static readonly Brush GraphBaseBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private readonly Line _graphBase = new() { Stroke = GraphBaseBrush };
    private readonly Polyline _graphLine = new() { Stroke = Brushes.White, StrokeThickness = 1 };

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public OverlayWindow()
    {
        InitializeComponent();
        ORows.ItemsSource = _rows;
        _onForeground = (_, _, hwnd, _, _, _, _) => { TrackGame(hwnd); KeepOnTop(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        SizeChanged += (_, _) => Reposition();
        // Só escuta a troca de janela ativa enquanto o overlay está na tela.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _hook == IntPtr.Zero)
            {
                _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
                TrackGame(GetForegroundWindow());
                KeepOnTop();
            }
            else if (!IsVisible) Unhook();
        };
        Closed += (_, _) => Unhook();
    }

    public void SetScale(double s) { Scale.ScaleX = Scale.ScaleY = s; }

    public void Reposition() => KeepOnTop();

    /// <summary>
    /// Cola no canto escolhido do monitor do jogo (área total, para ficar sobre jogos em tela cheia) e volta para o topo.
    /// Jogos em janela sem borda (GTA V, Cyberpunk 2077...) também se marcam como "sempre no topo" e, ao serem ativados,
    /// passam na frente; por isso isto roda a cada troca de janela ativa e a cada atualização do overlay.
    /// </summary>
    public void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        if (_monitor == IntPtr.Zero) _monitor = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(_monitor, ref mi) || !GetWindowRect(_hwnd, out var wr)) return;

        // Tudo em pixels do mesmo sistema de coordenadas (GetMonitorInfo e GetWindowRect), sem conversão de DPI.
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        int m = (int)Math.Round(12 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var r = mi.rcMonitor;
        int x = Corner.EndsWith("R") ? r.Right - w - m : r.Left + m;
        int y = Corner.StartsWith("B") ? r.Bottom - h - m : r.Top + m;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Segue o monitor da janela ativa, a não ser que seja do próprio tHg Optimization.</summary>
    private void TrackGame(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        _monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
    }

    private void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Atualiza o overlay. <paramref name="show"/> = itens escolhidos (chaves de OverlayMetrics).</summary>
    public void Update(FrameStats? f, IReadOnlyList<OverlayLine> lines, IReadOnlySet<string> show, string? extra)
    {
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        bool fps = show.Contains("fps"), ft = show.Contains("frametime");
        OFpsRow.Visibility = V(fps || ft);
        OFps.Visibility = OFpsUnit.Visibility = V(fps);
        OFrameTime.Visibility = V(ft);
        OFrameTime.Margin = new Thickness(fps ? 12 : 0, 0, 0, fps ? 4 : 0);
        OLow.Visibility = V(show.Contains("low1"));
        OGraph.Visibility = V(show.Contains("graph"));

        if (f != null)
        {
            OFps.Text = $"{f.Fps:0}";
            OFrameTime.Text = $"{f.FrameTimeMs:0.0} ms";
            OLow.Text = $"1% low {f.Low1Fps:0}";
        }
        else
        {
            OFps.Text = "—";
            OFrameTime.Text = fps ? "" : "— ms";
            OLow.Text = "aguardando jogo";
        }
        // Rodapé: nome do jogo e/ou hora.
        var foot = new List<string>();
        if (show.Contains("game") && f != null) foot.Add(f.ProcessName);
        if (extra != null) foot.Add(extra);
        OGame.Text = string.Join("  ·  ", foot);
        OGame.Visibility = V(foot.Count > 0);

        while (_rows.Count > lines.Count) _rows.RemoveAt(_rows.Count - 1);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i == _rows.Count) _rows.Add(new RowView());
            _rows[i].Label = lines[i].Label;
            _rows[i].Value = lines[i].Value;
        }
        ORows.Visibility = V(lines.Count > 0);
        if (OGraph.Visibility == Visibility.Visible) DrawGraph(f?.RecentFrameTimes);
    }

    /// <summary>Frametimes recentes: picos para cima = engasgos.</summary>
    private void DrawGraph(IReadOnlyList<double>? ft)
    {
        // A linha de base e a curva são criadas uma vez; a cada quadro só os pontos mudam.
        double w = OGraph.Width, h = OGraph.Height;
        if (OGraph.Children.Count == 0)
        {
            _graphBase.X1 = 0; _graphBase.X2 = w; _graphBase.Y1 = _graphBase.Y2 = h - 0.5;
            OGraph.Children.Add(_graphBase);
            OGraph.Children.Add(_graphLine);
        }
        if (ft == null || ft.Count < 2) { _graphLine.Visibility = Visibility.Collapsed; return; }

        double peak = 0, sum = 0;
        for (int i = 0; i < ft.Count; i++) { peak = Math.Max(peak, ft[i]); sum += ft[i]; }
        double max = Math.Max(peak, sum / ft.Count * 2); // escala que mostra picos sem achatar o normal
        var points = new PointCollection(ft.Count);
        double step = w / (ft.Count - 1);
        for (int i = 0; i < ft.Count; i++)
            points.Add(new Point(i * step, h - 1 - (h - 2) * Math.Min(1, ft[i] / max)));
        points.Freeze();
        _graphLine.Points = points;
        _graphLine.Visibility = Visibility.Visible;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
