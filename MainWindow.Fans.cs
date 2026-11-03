using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ThgOptimization;

/// <summary>Página Fans: curvas com histerese nas saídas de fan da placa-mãe (LibreHardwareMonitor).</summary>
public partial class MainWindow
{
    private readonly FanPageViewModel _fanPage = new();
    private List<FanChannelView> _fanViews => _fanPage.Channels;
    private FanControlEngine? _fanEngine;

    private async void InitFans()
    {
        var g = _settings.Fans;
        FanHyst.Text = g.HysteresisC.ToString("0.#", CultureInfo.InvariantCulture);
        FanEma.Text = g.EmaSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        FanDelay.Text = g.FallDelayS.ToString("0.#", CultureInfo.InvariantCulture);
        FanRise.Text = g.RiseRate.ToString("0.#", CultureInfo.InvariantCulture);
        FanFall.Text = g.FallRate.ToString("0.#", CultureInfo.InvariantCulture);
        if (!_monitor.IsOpen) { FanStatus.Text = "Lendo o hardware… abra esta página de novo em alguns segundos."; return; }
        try
        {
            await _monitor.RefreshMotherboardAsync(); // o Super I/O leva de 10 a 50 ms: fora da janela
            if (_exiting) return;
            _ = EnsureTlHubAsync();
            BuildFanViews();
            if (PageFans.IsVisible) ShowFanList();
        }
        catch (Exception ex) { Infrastructure.AppLog.Error("fans", ex); }
    }

    private void ShowFanList()
    {
        FanStatus.Text = _fanViews.Count == 0
            ? "Nenhuma saída de fan controlável foi encontrada na placa-mãe. Algumas placas (principalmente notebooks e algumas Gigabyte) não liberam o controle; " +
              "fans ligadas em hubs (Lian Li, Corsair) são controladas pelo hub, não pela placa-mãe."
            : $"{_fanViews.Count} saída(s) de fan na placa-mãe. Fans em hubs da Lian Li ou Corsair só obedecem daqui se o hub estiver no modo \"sincronizar com a placa-mãe\" (MB Sync).";
        RenderFanList();
    }

    private TlFanHub? _tlHub;
    private bool _tlHubTried;

    /// <summary>
    /// Abre a controladora Lian Li fora da janela (SetupAPI e o handshake levam de 50 ms a 1,5 s). Só com os sensores abertos:
    /// BuildFanViews só roda de novo com a lista vazia, e o hub sozinho na lista deixaria as saídas da placa-mãe de fora.
    /// </summary>
    /// <summary>Não lança: quem chama descarta a tarefa. O app fechando durante a abertura solta o handle HID (ver UiGuard).</summary>
    private async Task EnsureTlHubAsync()
    {
        if (_tlHubTried) return;
        _tlHubTried = true;
        try
        {
            var hub = await Infrastructure.UiGuard.OpenUnlessExitingAsync("fans", TlFanHub.Open, () => _exiting);
            if (hub == null) return;
            _tlHub = hub;
            BuildFanViews(); // as portas entram agora, sem esperar a próxima visita à página
            if (PageFans.IsVisible) ShowFanList();
        }
        catch (Exception ex) { Infrastructure.AppLog.Error("fans", ex); }
    }

    private void BuildFanViews()
    {
        var outputs = _monitor.FanOutputs.ToList();
        // Controladora Lian Li UNI FAN TL: as portas entram como saídas de fan (o L-Connect 3 precisa estar fechado).
        if (_tlHub != null) outputs.AddRange(_tlHub.Outputs());
        foreach (var o in outputs)
        {
            if (_fanViews.Any(v => v.Output.Id == o.Id)) continue;
            var cfg = _settings.Fans.Channels.FirstOrDefault(c => c.Id == o.Id);
            if (cfg == null)
            {
                var created = cfg = new FanChannelSettings { Id = o.Id };
                _settings.Mutate(() => _settings.Fans.Channels.Add(created));
            }
            _fanViews.Add(new FanChannelView(o, cfg));
        }
        if (_fanViews.Count == 0) return;
        if (_fanEngine == null)
        {
            _fanEngine = new FanControlEngine(_monitor.ReadFanInputs, () => _settings.Fans, () => GpuBenchActive);
            _fanEngine.SensorStateChanged += unavailable => Dispatcher.BeginInvoke(() =>
            {
                if (_exiting) return;
                if (unavailable) Toast("Fans: sensores sem leitura", "As curvas foram suspensas e o controle voltou à BIOS. Elas retomam quando a leitura voltar.", "");
                else Toast("Fans: leitura de volta", "As curvas foram retomadas.", "");
            });
        }
        _fanEngine.SetChannels(_fanViews);
        _fanEngine.Start();
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
        g.EmaSeconds = Num(FanEma, g.EmaSeconds, 0.5, 30);
        g.FallDelayS = Num(FanDelay, g.FallDelayS, 0, 120);
        g.RiseRate = Num(FanRise, g.RiseRate, 1, 100);
        g.FallRate = Num(FanFall, g.FallRate, 0.5, 100);
        SaveFans();
    }

    private void SaveFans() { try { _settings.Save(); } catch { } }

    /// <summary>
    /// Parte da janela nas curvas de fan, chamada pelo timer dela: acha as saídas de curvas salvas na abertura do app e, só com a
    /// página aberta, mostra o que o FanEngine já calculou. Nenhum acesso a hardware aqui.
    /// </summary>
    private void FanUiTick()
    {
        if (_exiting || !_monitor.IsOpen) return;
        if (_fanViews.Count == 0 && _settings.Fans.Channels.Any(c => c.Preset != "bios")) BuildFanViews();
        _fanPage.Publish(_fanEngine, PageFans.IsVisible);
    }
}
