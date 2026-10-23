using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ThgOptimization;

/// <summary>Página Fans: curvas com histerese nas saídas de fan da placa-mãe (LibreHardwareMonitor).</summary>
public partial class MainWindow
{
    private readonly List<FanChannelView> _fanViews = new();
    private DateTime _fanLast;

    private void InitFans()
    {
        var g = _settings.Fans;
        FanHyst.Text = g.HysteresisC.ToString("0.#", CultureInfo.InvariantCulture);
        FanDelay.Text = g.FallDelayS.ToString("0.#", CultureInfo.InvariantCulture);
        FanRise.Text = g.RiseRate.ToString("0.#", CultureInfo.InvariantCulture);
        FanFall.Text = g.FallRate.ToString("0.#", CultureInfo.InvariantCulture);
        if (!_monitor.IsOpen) { FanStatus.Text = "Lendo o hardware… abra esta página de novo em alguns segundos."; return; }
        _monitor.RefreshMotherboard();
        BuildFanViews();
        FanStatus.Text = _fanViews.Count == 0
            ? "Nenhuma saída de fan controlável foi encontrada na placa-mãe. Algumas placas (principalmente notebooks e algumas Gigabyte) não liberam o controle; " +
              "fans ligadas em hubs (Lian Li, Corsair) são controladas pelo hub, não pela placa-mãe."
            : $"{_fanViews.Count} saída(s) de fan na placa-mãe. Fans em hubs da Lian Li ou Corsair só obedecem daqui se o hub estiver no modo \"sincronizar com a placa-mãe\" (MB Sync).";
        RenderFanList();
    }

    private TlFanHub? _tlHub;
    private bool _tlHubTried;

    private void BuildFanViews()
    {
        var outputs = _monitor.FanOutputs.ToList();
        // Controladora Lian Li UNI FAN TL: as portas entram como saídas de fan (o L-Connect 3 precisa estar fechado).
        if (!_tlHubTried) { _tlHubTried = true; try { _tlHub = TlFanHub.Open(); } catch { _tlHub = null; } }
        if (_tlHub != null) outputs.AddRange(_tlHub.Outputs());
        foreach (var o in outputs)
        {
            if (_fanViews.Any(v => v.Output.Id == o.Id)) continue;
            var cfg = _settings.Fans.Channels.FirstOrDefault(c => c.Id == o.Id);
            if (cfg == null) { cfg = new FanChannelSettings { Id = o.Id }; _settings.Fans.Channels.Add(cfg); }
            _fanViews.Add(new FanChannelView(o, cfg));
        }
    }

    private void RenderFanList()
    {
        FanList.Children.Clear();
        foreach (var v in _fanViews)
        {
            var card = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 12) };
            var sp = new StackPanel();
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = v.Title, FontWeight = FontWeights.SemiBold });
            var live = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontFamily = (System.Windows.Media.FontFamily)FindResource("Mono") };
            live.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FanChannelView.Live)) { Source = v });
            head.Children.Add(live);
            sp.Children.Add(head);

            sp.Children.Add(Caption("Curva"));
            sp.Children.Add(Chips(FanCurves.Presets, v.Preset, k => { v.Preset = k; SaveFans(); }, "fanp" + v.Output.Id));
            sp.Children.Add(Caption("Temperatura que manda"));
            sp.Children.Add(Chips(FanCurves.Sources, v.Source, k => { v.Source = k; SaveFans(); }, "fans" + v.Output.Id));
            sp.Children.Add(Caption("Curva personalizada (°C:% separados por vírgula)"));
            var custom = new TextBox { Text = v.Custom, Margin = new Thickness(0, 4, 0, 0) };
            custom.LostFocus += (_, _) => { v.Custom = custom.Text; if (v.Preset != "custom") v.Preset = "custom"; SaveFans(); RenderFanList(); };
            sp.Children.Add(custom);
            card.Child = sp;
            FanList.Children.Add(card);
        }
    }

    private static TextBlock Caption(string t) => new() { Text = t, FontSize = 12, Margin = new Thickness(0, 10, 0, 4), Opacity = 0.7 };

    private WrapPanel Chips((string Key, string Label)[] opts, string current, Action<string> pick, string group)
    {
        var wp = new WrapPanel();
        foreach (var (key, label) in opts)
        {
            var rb = new RadioButton { Content = label.ToUpperInvariant(), GroupName = group, IsChecked = key == current, Style = (Style)FindResource("Chip") };
            string k = key;
            rb.Checked += (_, _) => pick(k);
            wp.Children.Add(rb);
        }
        return wp;
    }

    private void FanGlobal_Changed(object sender, RoutedEventArgs e)
    {
        static double Num(TextBox t, double def, double min, double max) =>
            double.TryParse(t.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : def;
        var g = _settings.Fans;
        g.HysteresisC = Num(FanHyst, g.HysteresisC, 0, 20);
        g.FallDelayS = Num(FanDelay, g.FallDelayS, 0, 120);
        g.RiseRate = Num(FanRise, g.RiseRate, 1, 100);
        g.FallRate = Num(FanFall, g.FallRate, 0.5, 100);
        SaveFans();
    }

    private void SaveFans() { try { _settings.Save(); } catch { } }

    /// <summary>Chamado a cada segundo pelo timer da janela (também com o app na bandeja e durante jogos).</summary>
    private void FanTick()
    {
        if (!_monitor.IsOpen) return;
        if (_fanViews.Count == 0)
        {
            if (!_settings.Fans.Channels.Any(c => c.Preset != "bios")) return;
            BuildFanViews(); // curvas salvas: assume o controle desde a abertura do app
            if (_fanViews.Count == 0) return;
        }
        double dt = _fanLast == default ? 1 : Math.Clamp((DateTime.Now - _fanLast).TotalSeconds, 0.2, 5);
        _fanLast = DateTime.Now;
        if (PageFans.IsVisible) _monitor.RefreshMotherboard();
        float? cpu = _monitor.CpuTemperature, gpu = _monitor.GpuTemperature;
        foreach (var v in _fanViews) v.Live = FanCurves.Step(v, _settings.Fans, cpu, gpu, dt);
    }
}
