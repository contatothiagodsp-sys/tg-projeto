using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ThgOptimization;

/// <summary>
/// Página Telas LCD: cada tela reconhecida aparece como um cartão próprio. A tela escolhida mostra a sua configuração
/// (imagem/GIF, encaixe, sensores, brilho, rotação) e a prévia na resolução dela; "Aplicar nesta tela" manda só para ela
/// e "Aplicar em todas" manda o conteúdo de cada uma para a própria tela. A atualização contínua faz o mesmo a cada segundo.
/// O diagnóstico USB (VID/PID, relatório) fica na página Avançado → Diagnóstico USB.
/// </summary>
public partial class MainWindow
{
    /// <summary>Uma tela de destino: aparelho real (fan TL LCD, TURZX, Turing por COM) ou "arquivo" (só salvar o quadro).</summary>
    private sealed class LcdScreen
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string Kind { get; init; } // "tl", "turzx", "turingA", "file"
        public required LcdTarget Target { get; set; }
        public string? Com { get; init; }
        public TlLcdScreen? Tl { get; set; }
        public LcdStudio Studio { get; } = new();
        public string? LoadedImage { get; set; }
        public string State { get; set; } = "";
        public bool? Ok { get; set; }
        public TextBlock? StateText { get; set; }
        public RadioButton? Card { get; set; }
    }

    private readonly List<LcdScreen> _lcdScreens = new();
    private LcdScreen? _lcdSel;
    private bool _lcdLoading;
    private DispatcherTimer? _lcdTimer;
    private List<UsbDev> _usb2 = new();
    private bool _lcdInit;
    private DispatcherTimer? _lcdLoopTimer;
    private bool _lcdSending;
    private readonly Dictionary<string, TuringSerial> _serials = new();
    private TuringRevC? _turzx;

    private async void InitLcd()
    {
        if (!_lcdInit)
        {
            _lcdInit = true;
            foreach (var t in LcdStudio.Targets)
            {
                var rb = new RadioButton { Content = t.Label, GroupName = "LcdTarget", Style = (Style)FindResource("Chip"), Tag = t.Key,
                                           IsChecked = t.Key == _settings.LcdTarget };
                rb.Checked += (_, _) =>
                {
                    _settings.LcdTarget = t.Key; _settings.Save();
                    if (_lcdSel?.Kind == "file") { _lcdSel.Target = t; RenderLcd(); }
                };
                LcdTargets.Children.Add(rb);
            }
            _lcdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            DateTime lastStatic = default;
            _lcdTimer.Tick += (_, _) =>
            {
                if (!PageLcd.IsVisible) { _lcdTimer.Stop(); return; }
                // GIF: a cada 100 ms; imagem parada: só 1x por segundo (sensores).
                if (_lcdSel?.Studio.Animated == true || (DateTime.Now - lastStatic).TotalSeconds >= 1) { lastStatic = DateTime.Now; RenderLcd(); }
            };
            await ScanUsbDevicesAsync();
            await RefreshLcdScreensAsync();
        }
        _lcdTimer?.Start();
        RenderLcd();
    }

    // ---------------- diagnóstico USB (página Avançado) ----------------

    private async Task ScanUsbDevicesAsync()
    {
        UsbDevSummary.Text = "Procurando…";
        try { _usb2 = await Task.Run(UsbInventory.Scan); }
        catch (Exception ex) { UsbDevSummary.Text = "Não foi possível listar: " + ex.Message; return; }
        var known = _usb2.Where(d => d.Known != null).GroupBy(d => (d.Vid, d.Pid)).Select(g => g.First()).ToList();
        UsbDevSummary.Text = known.Count == 0
            ? $"{_usb2.Count} dispositivos USB/HID, nenhuma tela ou controladora de fans conhecida. Salve o relatório e mande para análise."
            : "Encontrado: " + string.Join(" · ", known.Select(k => $"{k.Known!.Brand} {k.Known.Model} ({k.Vid}:{k.Pid})"));
        UsbDevText.Text = UsbInventory.Report(_usb2);
    }

    private async void BtnUsbDevScan_Click(object sender, RoutedEventArgs e) => await ScanUsbDevicesAsync();

    private void BtnUsbReport_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"tHg - USB {DateTime.Now:yyyyMMdd-HHmm}.txt");
        try
        {
            File.WriteAllText(path, UsbInventory.Report(_usb2));
            UsbDevSummary.Text = $"Relatório salvo na Área de Trabalho: {Path.GetFileName(path)}";
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) { UsbDevSummary.Text = "Não foi possível salvar: " + ex.Message; }
    }

    // ---------------- telas encontradas ----------------

    private static readonly (string Vid, string Pid)[] TurzxIds = { ("1A86", "CA21"), ("1D6B", "0121"), ("1D6B", "0106"), ("0525", "A4A7") };

    private async void BtnLcdRescan_Click(object sender, RoutedEventArgs e)
    {
        await ScanUsbDevicesAsync();
        await RefreshLcdScreensAsync();
    }

    /// <summary>Monta a lista de telas: abre as fans TL LCD (lendo a posição de cada uma), a TURZX e as Turing por COM.</summary>
    private async Task RefreshLcdScreensAsync()
    {
        if (_lcdSending) { LcdScreenInfo.Text = "Aguarde o envio atual terminar e procure de novo."; return; }
        _lcdSending = true;
        LcdScreenInfo.Text = "Procurando telas…";
        string? keep = _lcdSel?.Key;
        try
        {
            foreach (var s in _lcdScreens) s.Tl?.Dispose();
            _lcdScreens.Clear();

            List<TlLcdScreen> tls = new();
            string? tlError = null;
            if (_usb2.Any(d => d.Vid == "04FC" && d.Pid == "7393"))
                try { tls = await Task.Run(TlLcdScreen.OpenAll); } catch (Exception ex) { tlError = ex.Message; }

            var tl400 = LcdStudio.Targets.First(t => t.Key == "lianli400");
            int n = 0;
            foreach (var t in tls)
            {
                n++;
                // Sem posição informada pela fan: numera na ordem do Windows (pode mudar se trocar a porta USB).
                string label = t.Port >= 0 ? t.Label : $"Fan TL LCD {n}";
                _lcdScreens.Add(new LcdScreen { Key = t.Key, Label = label, Kind = "tl", Target = tl400, Tl = t, State = "conectada", Ok = true });
            }
            int tlUsb = _usb2.Count(d => d.Vid == "04FC" && d.Pid == "7393" && d.Interface == "" && d.Class == "USB");
            if (tlUsb > tls.Count)
                tlError = $"{tlUsb} fan(s) TL LCD no USB, {tls.Count} abriram para envio" + (tlError != null ? $" ({tlError})" : "") + ".";

            if (_usb2.Any(d => d.ComPort != null && TurzxIds.Contains((d.Vid, d.Pid))))
            {
                bool asleep = _usb2.Any(d => (d.Vid, d.Pid) == ("1A86", "CA21")) && !_usb2.Any(d => TurzxIds.Skip(1).Contains((d.Vid, d.Pid)));
                _lcdScreens.Add(new LcdScreen
                {
                    Key = "turzx", Label = "TURZX redonda 2,1\"/2,8\"", Kind = "turzx", Target = LcdStudio.Targets.First(t => t.Key == "round480"),
                    State = _turzx != null ? $"conectada ({_turzx.Port})" : asleep ? "em espera (acorda ao enviar)" : "encontrada", Ok = _turzx != null ? true : null,
                });
            }

            foreach (var d in _usb2.Where(d => d.ComPort != null && d.Vid == "1A86" && d.Pid is "5722" or "7523").GroupBy(d => d.ComPort).Select(g => g.First()))
            {
                var k = d.Known;
                var target = (k != null ? LcdStudio.Targets.FirstOrDefault(t => t.Width == k.Width && t.Height == k.Height) : null)
                             ?? LcdStudio.Targets.First(t => t.Key == "turing35");
                _lcdScreens.Add(new LcdScreen { Key = "com:" + d.ComPort, Label = $"{k?.Model ?? "Tela serial"} ({d.ComPort})", Kind = "turingA", Com = d.ComPort, Target = target, State = "encontrada" });
            }

            var fileTarget = LcdStudio.Targets.FirstOrDefault(t => t.Key == _settings.LcdTarget) ?? LcdStudio.Targets[0];
            _lcdScreens.Add(new LcdScreen { Key = "file", Label = "Só salvar arquivo", Kind = "file", Target = fileTarget, State = "sem envio" });

            int real = _lcdScreens.Count - 1;
            bool slWireless = _usb2.Any(d => d.Vid == "1CBE" && d.Pid == "0005");
            LcdScreenInfo.Text = (real == 0 ? "Nenhuma tela compatível conectada. Dá para montar a imagem e salvar o quadro como arquivo." :
                                  $"{real} tela{(real > 1 ? "s" : "")} encontrada{(real > 1 ? "s" : "")}.") +
                                 (tlError != null ? " " + tlError : "") +
                                 (slWireless ? " Receptor do UNI FAN SL Wireless (1CBE:0005) encontrado: o envio de imagem por esse receptor ainda não é suportado." : "") +
                                 (LConnectRunning() ? " O L-Connect 3 está aberto e pode sobrescrever as fans: feche-o antes de aplicar." : "");
        }
        finally { _lcdSending = false; }

        BuildLcdCards();
        SelectLcdScreen(_lcdScreens.FirstOrDefault(s => s.Key == keep) ?? _lcdScreens[0]);
    }

    private void BuildLcdCards()
    {
        LcdScreenCards.Children.Clear();
        foreach (var s in _lcdScreens)
        {
            var title = new TextBlock { Text = s.Label, FontWeight = FontWeights.SemiBold };
            var res = new TextBlock { Text = s.Kind == "file" ? "monta e salva JPEG" : $"{s.Target.Width}×{s.Target.Height}{(s.Target.Round ? " redonda" : "")}", FontSize = 11, Opacity = 0.65 };
            s.StateText = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            var card = new RadioButton
            {
                Style = (Style)FindResource("SelectCard"), GroupName = "LcdScreen", Tag = s.Key, Margin = new Thickness(0, 0, 8, 8),
                Content = new StackPanel { Children = { title, res, s.StateText } },
            };
            var screen = s;
            card.Checked += (_, _) => { if (!_lcdLoading) SelectLcdScreen(screen); };
            s.Card = card;
            LcdScreenCards.Children.Add(card);
            ShowLcdState(s);
        }
    }

    private void ShowLcdState(LcdScreen s)
    {
        if (s.StateText == null) return;
        var cfg = LcdCfg(s);
        string extra = s.Ok == true && cfg.LastSent is DateTime t && s.Kind != "file" ? $" · enviada {t:dd/MM HH:mm}" : "";
        s.StateText.Text = (s.Ok == true ? "● " : s.Ok == false ? "✕ " : "○ ") + s.State + extra;
        s.StateText.SetResourceReference(TextBlock.ForegroundProperty, s.Ok == true ? "Good" : s.Ok == false ? "Bad" : "Muted");
    }

    /// <summary>Configuração salva da tela. A primeira vez herda a imagem e a rotação usadas nas versões anteriores.</summary>
    private LcdScreenConfig LcdCfg(LcdScreen s)
    {
        if (!_settings.LcdScreens.TryGetValue(s.Key, out var c))
        {
            c = new LcdScreenConfig { Image = _settings.LcdImage, Rotation = s.Kind == "turzx" ? _settings.TurzxRotation : 0 };
            _settings.LcdScreens[s.Key] = c;
        }
        return c;
    }

    /// <summary>Carrega a imagem configurada no estúdio da tela (só se mudou).</summary>
    private void EnsureLcdImage(LcdScreen s)
    {
        var cfg = LcdCfg(s);
        if (s.LoadedImage == cfg.Image) return;
        s.LoadedImage = cfg.Image;
        s.Studio.Clear();
        if (cfg.Image is string img && File.Exists(img)) try { s.Studio.Load(img); } catch { }
    }

    private void SelectLcdScreen(LcdScreen s)
    {
        _lcdSel = s;
        var cfg = LcdCfg(s);
        EnsureLcdImage(s);
        _lcdLoading = true;
        try
        {
            if (s.Card != null) s.Card.IsChecked = true;
            LcdTargetsBox.Visibility = s.Kind == "file" ? Visibility.Visible : Visibility.Collapsed;
            LcdEditTitle.Text = "IMAGEM DE: " + s.Label.ToUpperInvariant();
            (cfg.Fill ? LcdFill : LcdFit).IsChecked = true;
            LcdCpuT.IsChecked = cfg.CpuTemp; LcdGpuT.IsChecked = cfg.GpuTemp;
            LcdCpuL.IsChecked = cfg.CpuLoad; LcdGpuL.IsChecked = cfg.GpuLoad; LcdClock.IsChecked = cfg.Clock;
            foreach (RadioButton rb in LcdBrightChips.Children) rb.IsChecked = rb.Tag as string == cfg.Brightness.ToString();
            bool square = s.Target.Width == s.Target.Height;
            foreach (RadioButton rb in LcdRotChips.Children)
            {
                int deg = int.Parse((string)rb.Tag);
                rb.IsEnabled = square || deg % 180 == 0; // tela retangular: girar 90° mudaria o tamanho
                rb.IsChecked = deg == cfg.Rotation;
            }
            BtnLcdSend.IsEnabled = s.Kind != "file";
            BtnLcdSend.Content = s.Kind == "file" ? "ESCOLHA UMA TELA" : "APLICAR NESTA TELA";
            BtnLcdSendAll.IsEnabled = _lcdScreens.Any(x => x.Kind != "file");
        }
        finally { _lcdLoading = false; }
        LcdStatus.Text = cfg.Image != null ? Path.GetFileName(cfg.Image) + (s.Studio.Animated ? " (GIF animado)" : "") : "Sem imagem: fundo preto com os sensores.";
        RenderLcd();
    }

    // ---------------- edição da tela escolhida ----------------

    private void SaveLcdCfg() { try { _settings.Save(); } catch { } }

    private void BtnLcdPick_Click(object sender, RoutedEventArgs e)
    {
        if (_lcdSel is not { } s) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Imagem ou GIF para: {s.Label}",
            Filter = "Imagens e GIFs|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var test = new LcdStudio();
            test.Load(dlg.FileName); // valida antes de trocar a configuração
            LcdCfg(s).Image = dlg.FileName; SaveLcdCfg();
            EnsureLcdImage(s);
            LcdStatus.Text = $"{Path.GetFileName(dlg.FileName)}{(s.Studio.Animated ? " (GIF animado)" : "")} em {s.Label}. Clique em APLICAR NESTA TELA para enviar.";
        }
        catch (Exception ex) { LcdStatus.Text = "Não foi possível abrir: " + ex.Message; }
        RenderLcd();
    }

    private void BtnLcdClear_Click(object sender, RoutedEventArgs e)
    {
        if (_lcdSel is not { } s) return;
        LcdCfg(s).Image = null; SaveLcdCfg(); EnsureLcdImage(s); RenderLcd();
        LcdStatus.Text = $"Imagem removida de {s.Label} (as outras telas não mudam).";
    }

    private void LcdOption_Changed(object sender, RoutedEventArgs e)
    {
        if (LcdCpuT == null || _lcdLoading || _lcdSel == null) return; // ainda montando a janela
        var c = LcdCfg(_lcdSel);
        c.CpuTemp = LcdCpuT.IsChecked == true; c.GpuTemp = LcdGpuT.IsChecked == true;
        c.CpuLoad = LcdCpuL.IsChecked == true; c.GpuLoad = LcdGpuL.IsChecked == true;
        c.Clock = LcdClock.IsChecked == true; c.Fill = LcdFill.IsChecked == true;
        SaveLcdCfg();
        RenderLcd();
    }

    private void LcdBright_Checked(object sender, RoutedEventArgs e)
    {
        if (_lcdLoading || _lcdSel == null) return;
        if (sender is RadioButton { Tag: string t } && int.TryParse(t, out int v)) { LcdCfg(_lcdSel).Brightness = v; SaveLcdCfg(); }
    }

    private void LcdRot_Checked(object sender, RoutedEventArgs e)
    {
        if (_lcdLoading || _lcdSel == null) return;
        if (sender is RadioButton { Tag: string t } && int.TryParse(t, out int v)) { LcdCfg(_lcdSel).Rotation = v; SaveLcdCfg(); }
    }

    private LcdSensors LcdSensorValues()
    {
        float? gpuLoad = _monitor.Rows.FirstOrDefault(r => r.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.GpuAmd
                                                               or LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia
                                                               or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel
                                                            && r.Type == LibreHardwareMonitor.Hardware.SensorType.Load && r.Name.Contains("Core"))?.RawValue;
        return new LcdSensors(_monitor.CpuTemperature, _monitor.GpuTemperature, _monitor.CpuLoad, gpuLoad);
    }

    /// <summary>Quadro da tela com a configuração dela (sem a rotação: a prévia mostra como fica de frente).</summary>
    private BitmapSource RenderScreen(LcdScreen s, LcdSensors sensors)
    {
        EnsureLcdImage(s);
        return s.Studio.Render(s.Target, LcdCfg(s).Overlay(), sensors);
    }

    private void RenderLcd()
    {
        if (_lcdSel is not { } s) return;
        try
        {
            LcdPreview.Source = RenderScreen(s, LcdSensorValues());
            LcdRes.Text = $"{s.Label} · {s.Target.Width} × {s.Target.Height}{(s.Target.Round ? " · redonda" : "")}";
        }
        catch (Exception ex) { LcdStatus.Text = "Prévia: " + ex.Message; }
    }

    private void BtnLcdSave_Click(object sender, RoutedEventArgs e)
    {
        if (_lcdSel is not { } s) return;
        try
        {
            var frame = RenderScreen(s, LcdSensorValues());
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"tHg - tela {DateTime.Now:HHmmss}.jpg");
            File.WriteAllBytes(path, LcdStudio.ToJpeg(frame));
            LcdStatus.Text = "Quadro salvo na Área de Trabalho (dá para usar no software da TURZX ou no L-Connect enquanto isso).";
        }
        catch (Exception ex) { LcdStatus.Text = "Não foi possível salvar: " + ex.Message; }
    }

    // ---------------- envio ----------------

    /// <summary>JPEG de até 64 KB (limite das telas TL): baixa a qualidade até caber.</summary>
    private static byte[] JpegUnder(BitmapSource f, int max)
    {
        for (int q = 90; q >= 40; q -= 10)
        {
            var j = LcdStudio.ToJpeg(f, q);
            if (j.Length <= max) return j;
        }
        return LcdStudio.ToJpeg(f, 30);
    }

    private static bool LConnectRunning() =>
        System.Diagnostics.Process.GetProcesses().Any(p => { try { return p.ProcessName.Replace(" ", "").StartsWith("L-Connect", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("LConnect", StringComparison.OrdinalIgnoreCase); } catch { return false; } finally { p.Dispose(); } });

    private async void BtnLcdSend_Click(object sender, RoutedEventArgs e)
    {
        if (_lcdSel is { Kind: not "file" } s) await SendToScreensAsync(new[] { s }, staticImage: true);
    }

    private async void BtnLcdSendAll_Click(object sender, RoutedEventArgs e) =>
        await SendToScreensAsync(_lcdScreens.Where(s => s.Kind != "file").ToList(), staticImage: true);

    /// <summary>Dados prontos para uma tela (montados na thread da janela, enviados em segundo plano).</summary>
    private sealed record LcdJob(LcdScreen Screen, byte[] Data, int Brightness);

    private async Task SendToScreensAsync(IReadOnlyList<LcdScreen> screens, bool staticImage)
    {
        if (_lcdSending || screens.Count == 0) return;
        _lcdSending = true;
        if (staticImage) { BtnLcdSend.IsEnabled = BtnLcdSendAll.IsEnabled = false; LcdStatus.Text = "Enviando…"; }
        try
        {
            var sensors = LcdSensorValues();
            var jobs = new List<LcdJob>();
            foreach (var s in screens)
            {
                var cfg = LcdCfg(s);
                var frame = RenderScreen(s, sensors);
                byte[] data = s.Kind switch
                {
                    "tl" => JpegUnder(LcdStudio.Rotate(frame, cfg.Rotation), TlLcdScreen.MaxJpeg),
                    "turzx" => LcdStudio.ToBgra(frame, cfg.Rotation),
                    "turingA" => LcdStudio.ToRgb565(LcdStudio.Rotate(frame, cfg.Rotation)),
                    _ => Array.Empty<byte>(),
                };
                if (data.Length > 0) jobs.Add(new LcdJob(s, data, cfg.Brightness));
            }

            var results = await Task.Run(() => jobs.Select(j => (j.Screen, Error: SendOne(j, staticImage))).ToList());

            foreach (var (s, err) in results)
            {
                s.Ok = err == null;
                if (err == null)
                {
                    if (staticImage) LcdCfg(s).LastSent = DateTime.Now;
                    s.State = staticImage ? "imagem aplicada" : "ao vivo";
                }
                else s.State = err;
                ShowLcdState(s);
            }
            if (staticImage)
            {
                SaveLcdCfg();
                int ok = results.Count(r => r.Error == null);
                LcdStatus.Text = (results.Count == 1
                                     ? results[0].Error == null ? $"{results[0].Screen.Label}: imagem aplicada. As outras telas não mudaram." : $"{results[0].Screen.Label}: {results[0].Error}"
                                     : $"{ok} de {results.Count} telas atualizadas, cada uma com o seu conteúdo." +
                                       string.Concat(results.Where(r => r.Error != null).Select(r => $" {r.Screen.Label}: {r.Error}"))) +
                                 (LConnectRunning() ? " O L-Connect 3 está aberto e pode sobrescrever as telas: feche-o (ou ative \"Pausar programas\" no Modo Jogo)." : "");
            }
        }
        catch (Exception ex) { if (staticImage) LcdStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _lcdSending = false;
            if (_lcdSel != null) { BtnLcdSend.IsEnabled = _lcdSel.Kind != "file"; BtnLcdSendAll.IsEnabled = _lcdScreens.Any(x => x.Kind != "file"); }
        }
    }

    /// <summary>Envia para uma tela. Devolve null se deu certo ou o motivo do erro (texto curto para o cartão).</summary>
    private string? SendOne(LcdJob j, bool staticImage)
    {
        var s = j.Screen;
        try
        {
            switch (s.Kind)
            {
                case "tl":
                    if (s.Tl == null) return "desconectada: clique em PROCURAR TELAS DE NOVO";
                    if (staticImage) s.Tl.SendStatic(j.Data, j.Brightness); else s.Tl.SendFrame(j.Data);
                    return null;
                case "turzx":
                    if (_turzx == null)
                    {
                        string? com = TuringRevC.WakeAndFind(staticImage ? t => Dispatcher.BeginInvoke(() => LcdStatus.Text = t) : null);
                        if (com == null) return "a tela não acordou. Feche o app da TURZX e tente de novo.";
                        _turzx = TuringRevC.Open(com, j.Brightness);
                    }
                    else if (staticImage) _turzx.SetBrightness(j.Brightness);
                    _turzx.SendFrame480(j.Data);
                    return null;
                case "turingA":
                    // A porta fica aberta entre os quadros (abrir de novo a cada segundo pode reiniciar a tela).
                    if (!_serials.TryGetValue(s.Com!, out var ser)) _serials[s.Com!] = ser = TuringSerial.Open(s.Com!);
                    if (staticImage) ser.SetBrightness(j.Brightness);
                    ser.SendImage(j.Data, s.Target.Width, s.Target.Height);
                    return null;
            }
            return "sem envio";
        }
        catch (Exception ex)
        {
            switch (s.Kind)
            {
                case "tl": s.Tl?.Dispose(); s.Tl = null; break;
                case "turzx": _turzx?.Dispose(); _turzx = null; break;
                case "turingA": if (_serials.Remove(s.Com!, out var bad)) bad.Dispose(); break;
            }
            return ex.Message;
        }
    }

    private void LcdLoop_Click(object sender, RoutedEventArgs e)
    {
        if (LcdLoop.IsChecked == true)
        {
            _lcdLoopTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                async (_, _) => await SendToScreensAsync(_lcdScreens.Where(s => s.Kind != "file" && s.Ok != false).ToList(), staticImage: false), Dispatcher);
            _lcdLoopTimer.Start();
            LcdStatus.Text = "Atualização contínua ligada: cada tela recebe o seu conteúdo 1 vez por segundo, mesmo com o tHg na bandeja.";
        }
        else { _lcdLoopTimer?.Stop(); LcdStatus.Text = "Atualização contínua desligada."; }
    }
}
