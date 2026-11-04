using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization;

/// <summary>Dados do cartão de destaque do painel. Só notifica quando o valor muda (mesmo padrão de SensorRow.Update).</summary>
public sealed class DashViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private bool _sessionActive;
    private int _currentFps, _low1Fps, _benchScore;
    private TimeSpan _sessionDuration;

    public bool SessionActive { get => _sessionActive; set => Set(ref _sessionActive, value, SessionActiveArgs); }
    public int CurrentFps { get => _currentFps; set => Set(ref _currentFps, value, CurrentFpsArgs); }
    public int Low1Fps { get => _low1Fps; set => Set(ref _low1Fps, value, Low1FpsArgs); }
    public TimeSpan SessionDuration { get => _sessionDuration; set => Set(ref _sessionDuration, value, SessionDurationArgs); }
    public int BenchScore { get => _benchScore; set => Set(ref _benchScore, value, BenchScoreArgs); }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, System.ComponentModel.PropertyChangedEventArgs args) where T : IEquatable<T>
    {
        if (field.Equals(value)) return;
        field = value;
        PropertyChanged?.Invoke(this, args);
    }

    private static readonly System.ComponentModel.PropertyChangedEventArgs
        SessionActiveArgs = new(nameof(SessionActive)), CurrentFpsArgs = new(nameof(CurrentFps)), Low1FpsArgs = new(nameof(Low1Fps)),
        SessionDurationArgs = new(nameof(SessionDuration)), BenchScoreArgs = new(nameof(BenchScore));
}

/// <summary>
/// Visão geral: painel de blocos com gráfico de área (CPU × GPU), desempenho, tabela de componentes, anéis de
/// memória/disco, eventos recentes e lista de verificação da saúde do PC.
/// </summary>
public partial class MainWindow
{
    public sealed record DashComponent(string Icon, string Name, string Model, string Temp, string Load, string Clock, string Power, string Target);
    public sealed record DashEvent(string Icon, string Title, string Detail, string Value, DateTime At)
    {
        public string When => At.Date == DateTime.Today ? $"hoje {At:HH:mm}" : $"{At:dd/MM HH:mm}";
    }
    public sealed record DashRingItem(Brush Swatch, string Label, string Value);
    /// <summary>Item de "O que resolver": Page = onde resolver (clique leva até lá); Hint = dica ao passar o mouse.</summary>
    public sealed record DashCheck(string Title, string Detail, bool Ok, string? Page = null, string Hint = "");

    private readonly record struct DashSample(float? CpuT, float? GpuT, float? CpuL, float? GpuL, float? CpuW, float? GpuW);

    private const int DashMax = 900; // 15 min a 1 amostra/s
    private readonly List<DashSample> _dash = new();
    private readonly List<DashEvent> _events = new();
    private List<DashCheck> _health = new();
    private DateTime _healthAt = DateTime.MinValue;

    // ---------------- coleta (a cada segundo, mesmo com a página fechada) ----------------

    private float? MainGpuLoad() => _gpus.Count == 0 ? null :
        _monitor.Rows.FirstOrDefault(r => r.Hardware == _gpus[0].Name && r.Type == SensorType.Load && r.Name == "GPU Core")?.RawValue;

    private float? CpuClockAvg()
    {
        var c = _monitor.Rows.Where(r => r.HardwareType == HardwareType.Cpu && r.Type == SensorType.Clock &&
                                         r.Name.StartsWith("Core #") && r.RawValue > 0).Select(r => r.RawValue!.Value).ToList();
        return c.Count > 0 ? c.Average() : null;
    }

    private void CollectDashSample()
    {
        _dash.Add(new DashSample(_monitor.CpuTemperature, MainGpuTemp(), _monitor.CpuLoad, MainGpuLoad(), _monitor.CpuPower, MainGpuPower()));
        if (_dash.Count > DashMax) _dash.RemoveAt(0);
        if (PageDash.IsVisible) UpdateDashboard(full: false);
    }

    // ---------------- eventos recentes ----------------

    private void AddEvent(string icon, string title, string detail, string value)
    {
        _events.Insert(0, new DashEvent(icon, title, detail, value, DateTime.Now));
        if (_events.Count > 30) _events.RemoveAt(_events.Count - 1);
        if (PageDash.IsVisible) ShowEvents();
    }

    /// <summary>Eventos já registrados antes desta sessão (resultados de benchmark e verificações salvos).</summary>
    private IEnumerable<DashEvent> SavedEvents()
    {
        foreach (var r in _settings.CopyList(st => st.BenchRuns))
        {
            if (r.Multi is double m) yield return new DashEvent("", "BENCH", "Render · multi-core", $"{m:N0} pts", r.Date);
            else if (r.Single is double s) yield return new DashEvent("", "BENCH", "Render · single-core", $"{s:N0} pts", r.Date);
        }
        foreach (var r in _settings.CopyList(st => st.BenchRecords))
        {
            var (icon, title) = r.Test switch
            {
                "gpu" => ("", "Cena de jogo (GPU)"),
                "sim" => ("", "Simulação de jogo"),
                "mem" => ("", "Memória e carregamento"),
                "oc" => ("", "Verificação de estabilidade"),
                "disk" => ("", "Benchmark de disco"),
                _ => ("", r.Test)
            };
            string first = r.Summary.Split('·')[0].Trim();
            string rest = r.Summary.Contains('·') ? r.Summary[(r.Summary.IndexOf('·') + 1)..].Trim() : "";
            yield return new DashEvent(icon, title, rest, first, r.Date);
        }
    }

    private void ShowEvents()
    {
        var list = _events.Concat(SavedEvents()).OrderByDescending(e => e.At).Take(4).ToList();
        DashEvents.ItemsSource = list;
        DashEventsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- atualização ----------------

    private void DashOption_Checked(object sender, RoutedEventArgs e) { if (DashChart != null) DrawDashChart(); }
    private void DashChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDashChart();
    private void DashBench_Click(object sender, RoutedEventArgs e) => NavBench.IsChecked = true;
    private void DashOptimize_Click(object sender, RoutedEventArgs e) => NavigateTo(DashAction.Tag as string ?? "PageTurbo");

    private void DashCheck_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DashCheck { Page: string page } }) NavigateTo(page);
    }

    private void DashGo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string page }) return;
        var nav = new RadioButton[] { NavSensors, NavGpu, NavSystem, NavDisks }.FirstOrDefault(n => (string)n.Tag == page);
        if (nav != null) nav.IsChecked = true;
    }

    private void UpdateDashboard(bool full = true)
    {
        DrawDashChart();
        UpdateDashComponents();
        UpdateDashRings();
        if (full)
        {
            UpdateDashScores();
            ShowEvents();
        }
        if (full || (DateTime.Now - _healthAt).TotalSeconds > 60) UpdateDashHealth();
    }

    private static string F(float? v, string fmt, string unit) => v is float x ? x.ToString(fmt) + unit : "—";

    private void UpdateDashComponents()
    {
        var rows = new List<DashComponent>
        {
            new("", "Processador", GameCatalog.ShortCpuName(_cpuName),
                F(_monitor.CpuTemperature, "0", " °C"), F(_monitor.CpuLoad, "0", " %"), F(CpuClockAvg(), "0", " MHz"),
                F(_monitor.CpuPower, "0", " W"), "PageSensors"),
        };
        if (_gpus.Count > 0)
        {
            uint? clk = _gpus[0].NvmlHandle != IntPtr.Zero ? Nvml.Clock(_gpus[0].NvmlHandle, Nvml.ClockGraphics) : null;
            rows.Add(new("", "Placa de vídeo", GameCatalog.ShortGpuName(_gpus[0].Name),
                         F(MainGpuTemp(), "0", " °C"), F(MainGpuLoad(), "0", " %"), clk is uint c ? $"{c} MHz" : "—",
                         F(MainGpuPower(), "0", " W"), "PageGpu"));
        }
        var (total, avail) = Optimizer.MemoryStatus();
        if (total > 0)
        {
            double used = total - avail;
            rows.Add(new("", "Memória RAM", $"{total / 1073741824.0:0} GB instalados", "—", $"{used / total:P0}", "—",
                         $"{used / 1073741824.0:0.0} GB", "PageSystem"));
        }
        if (_drives.FirstOrDefault(d => d.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase)) is { } c0)
            rows.Add(new("", $"Disco {c0.Letter}", c0.Disk.Length > 0 ? c0.Disk : c0.Kind, "—", c0.UsedPercent, "—",
                         $"{DriveCard.Fmt(c0.Free)} livres", "PageDisks"));
        DashComponents.ItemsSource = rows;
    }

    private readonly DashViewModel _dashVm = new();

    /// <summary>
    /// Cartão de destaque durante o jogo. Mesma regra do desenho da janela (UiThrottle.SkipWindowDraw): com o jogo em foco, só
    /// troca de estado ao abrir ou fechar, sem ler o FPS a cada segundo para uma janela que ninguém está olhando.
    /// </summary>
    private void UpdateDashSession()
    {
        var gm = _gm;
        int? pid = gm?.ActiveGamePid;
        if (pid == null) { _dashVm.SessionActive = false; return; }
        if (_dashVm.SessionActive && !IsActive) return;
        _dashVm.SessionActive = true;
        _dashVm.SessionDuration = TimeSpan.FromSeconds(Math.Floor((DateTime.Now - gm!.ActiveSince).TotalSeconds));
        if (_frames.GetStats(pid.Value) is { } f)
        {
            _dashVm.CurrentFps = (int)Math.Round(f.Fps);
            _dashVm.Low1Fps = (int)Math.Round(f.Low1Fps);
        }
    }

    private void UpdateDashScores()
    {
        var allRuns = _settings.CopyList(st => st.BenchRuns);
        var records = _settings.CopyList(st => st.BenchRecords);
        var runs = allRuns.Where(r => r.Multi != null).ToList();
        if (runs.Count > 0)
        {
            DashScore.Text = $"{runs[0].Multi:N0}";
            DashScoreSub.Text = $"pts · render multi-core · {runs[0].Date:dd/MM HH:mm}";
            if (runs.Count > 1)
            {
                double before = runs[1].Multi!.Value, d = runs[0].Multi!.Value - before;
                double pct = before > 0 ? Math.Abs(d) / before * 100 : 0;
                SetDeltaPill(DashScorePill, DashScoreDelta, d, d >= 0 ? $"▲ {pct:0.0}%" : $"▼ {pct:0.0}%");
            }
            else SetDeltaPill(DashScorePill, DashScoreDelta, 0, "");
        }
        else { DashScore.Text = "—"; DashScoreDelta.Text = ""; DashScoreSub.Text = "rode o BENCH para ter uma nota"; }
        _dashVm.BenchScore = runs.Count > 0 ? (int)Math.Round(runs[0].Multi!.Value) : 0;

        string Last(string test) => records.FirstOrDefault(r => r.Test == test)?.Summary.Split('·')[0].Trim() ?? "—";
        var single = allRuns.FirstOrDefault(r => r.Single != null)?.Single;
        DashScores.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("Render single-core", single is double s ? $"{s:N0} pts" : "—"),
            new KeyValuePair<string, string>("Cena de jogo (GPU)", Last("gpu")),
            new KeyValuePair<string, string>("Simulação de jogo", Last("sim")),
        };
        DashCpuName.Text = GameCatalog.ShortCpuName(_cpuName);
        DashGpuName.Text = _gpus.Count > 0 ? GameCatalog.ShortGpuName(_gpus[0].Name) : "—";
    }

    /// <summary>
    /// Alerta em cartão: borda vermelha e LED no canto, com o texto nas cores normais. O cartão invertido fica reservado para
    /// um alerta crítico em tela cheia de jogo.
    /// </summary>
    private static void SetCardAlert(Border card, FrameworkElement led, bool alert, string normalBorder = "Border0Brush")
    {
        card.SetResourceReference(Border.BorderBrushProperty, alert ? "DangerBrush" : normalBorder);
        led.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Etiqueta de variação. goodness > 0 = verde (melhorou), &lt; 0 = vermelho (piorou), 0 = neutra.</summary>
    private void SetDeltaPill(System.Windows.Controls.Border pill, TextBlock text, double goodness, string label)
    {
        text.Text = label;
        pill.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string fg = goodness > 0 ? "Good" : goodness < 0 ? "Bad" : "Muted", bg = goodness > 0 ? "GoodSoft" : goodness < 0 ? "BadSoft" : "Hover";
        text.SetResourceReference(TextBlock.ForegroundProperty, fg);
        pill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, bg);
    }

    // ---------------- gráfico de área ----------------

    private Views.DashChartView? _dashChartView;

    private void DrawDashChart()
    {
        int range = int.Parse(CheckedTag(DashRangePanel) ?? "300");
        string metric = CheckedTag(DashMetricPanel) ?? "temp";
        Func<DashSample, float?> cpu = metric switch { "load" => s => s.CpuL, "power" => s => s.CpuW, _ => s => s.CpuT };
        Func<DashSample, float?> gpu = metric switch { "load" => s => s.GpuL, "power" => s => s.GpuW, _ => s => s.GpuT };
        string unit = metric switch { "load" => " %", "power" => " W", _ => " °C" };
        string name = metric switch { "load" => "CARGA", "power" => "CONSUMO", _ => "TEMPERATURA" };

        int start = Math.Max(0, _dash.Count - range), count = _dash.Count - start;
        var cpuValues = new float?[count];
        var gpuValues = new float?[count];
        float? firstC = null;
        double highest = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            var sample = _dash[start + i];
            var c = cpuValues[i] = cpu(sample);
            var g = gpuValues[i] = gpu(sample);
            firstC ??= c;
            if (c is float cv0 && cv0 > highest) highest = cv0;
            if (g is float gv0 && gv0 > highest) highest = gv0;
        }

        // Cabeçalho: valor atual grande + variação no período (como o par e a cotação da referência).
        float? cNow = count > 0 ? cpuValues[^1] : null, gNow = count > 0 ? gpuValues[^1] : null;
        DashChartLabel.Text = $"CPU · {name}";
        DashChartValue.Text = cNow is float cv ? $"{cv:0.#}{unit}" : "—";
        DashChartValue2.Text = gNow is float gv ? $"{gv:0.#}{unit}" : "—";
        string span = range >= 60 ? $"{range / 60} min" : $"{range} s";
        if (cNow is float a && firstC is float b)
        {
            double d = Math.Round(a - b, 1);
            // Temperatura, carga e consumo: cair é bom (verde), subir é ruim (vermelho).
            SetDeltaPill(DashDeltaPill, DashChartDelta, -d, d == 0 ? $"= estável em {span}" : $"{(d > 0 ? "▲ +" : "▼ ")}{d:0.#}{unit} em {span}");
        }
        else SetDeltaPill(DashDeltaPill, DashChartDelta, 0,
                          cNow == null && metric != "load" ? "sem leitura da CPU (veja o aviso no menu)" : "coletando…");

        bool any = highest > double.MinValue;
        double yMin = metric == "temp" ? 20 : 0;
        double yMax = metric switch
        {
            "load" => 100,
            "power" => Math.Max(50, any ? highest * 1.2 : 50),
            _ => Math.Max(90, any ? highest + 5 : 90)
        };
        _dashChartView ??= new Views.DashChartView(DashChart, FindResource);
        _dashChartView.Draw(DashChart.ActualWidth, DashChart.ActualHeight, range, unit, yMin, yMax, cpuValues, gpuValues, DashChartValue.Text);
    }

    // ---------------- anéis concêntricos ----------------

    private Views.DashRingsView? _dashRingsView;
    private List<DashRingItem>? _dashRingLegend;

    private void UpdateDashRings()
    {
        var (total, avail) = Optimizer.MemoryStatus();
        double ram = total > 0 ? (double)(total - avail) / total : 0;
        double? vram = null;
        if (_gpus.Count > 0 && _gpus[0].NvmlHandle != IntPtr.Zero && Nvml.MemoryInfo(_gpus[0].NvmlHandle) is Nvml.Memory m && m.Total > 0)
            vram = (double)m.Used / m.Total;
        var c0 = _drives.FirstOrDefault(d => d.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase));

        DashMemHero.Text = total > 0 ? $"{(total - avail) / 1073741824.0:0.0} GB" : "—";
        DashMemSub.Text = total > 0 ? $"de RAM em uso · {total / 1073741824.0:0} GB instalados" : "";

        Brush fg = (Brush)FindResource("Fg"), muted = (Brush)FindResource("Muted"), faint = (Brush)FindResource("Faint");
        var rings = new List<(double Frac, Brush Brush, string Label)> { (ram, fg, "RAM") };
        if (vram is double v) rings.Add((v, muted, "VRAM"));
        if (c0 != null) rings.Add((c0.UsedFraction, faint, $"Disco {c0.Letter}"));

        _dashRingsView ??= new Views.DashRingsView(DashRings);
        _dashRingsView.Draw(rings.ConvertAll(r => (r.Frac, r.Brush)), $"{ram:P0}", (Brush)FindResource("Line"), fg, (FontFamily)FindResource("Mono"));

        // Trocar a fonte da lista recria os itens dela: só quando um valor mudou de verdade.
        var legend = rings.ConvertAll(r => new DashRingItem(r.Brush, r.Label, $"{r.Frac:P0}"));
        if (_dashRingLegend == null || !_dashRingLegend.SequenceEqual(legend))
        {
            _dashRingLegend = legend;
            DashRingLegend.ItemsSource = legend;
        }
    }

    // ---------------- saúde do PC ----------------

    /// <summary>
    /// "O que resolver": em vez de repetir ajustes que o TURBO já confere, junta os pontos que precisam de atenção do
    /// TURBO (desempenho) e do SHIELD (segurança), mais o que só o painel sabe (sensores, erros de hardware, portas COM).
    /// Cada item leva para onde se resolve. Sem pendências, diz isso claramente.
    /// </summary>
    private HardwareErrors? _hwErrs;
    private DateTime _hwErrsAt;

    private void UpdateDashHealth()
    {
        _healthAt = DateTime.Now;
        var list = new List<DashCheck>();

        string Score(TextBlock t, Border pill) => pill.Visibility == Visibility.Visible ? t.Text : "—";
        DashScoresLine.Text = $"Desempenho (TURBO) {Score(NavTurboScore, NavTurboScorePill)} · Segurança (SHIELD) {Score(NavShieldScore, NavShieldScorePill)}";

        // Pendências dos diagnósticos (mais graves primeiro).
        var problems = _checks.Where(c => c.IsProblem).Select(c => (c, page: c.Id.StartsWith("sec.") ? "PageShield" : "PageTurbo"))
            .Concat(_secChecks.Where(c => c.IsProblem).Select(c => (c, page: "PageShield")))
            .OrderBy(x => x.c.Level == CheckLevel.Bad ? 0 : 1).ThenByDescending(x => x.c.Weight).ToList();
        foreach (var (c, page) in problems.Take(4))
            list.Add(new DashCheck(c.Title, c.Found, false, page, c.Advice));
        if (problems.Count > 4)
            list.Add(new DashCheck($"Mais {problems.Count - 4} ponto(s)", "Veja a lista completa no TURBO e no SHIELD.", false, "PageTurbo"));

        // O que só o painel acompanha.
        if (_monitor.IsOpen)
        {
            var sensors = _monitor.DiagnoseCpuSensors();
            if (sensors != SensorMonitor.CpuSensorProblem.None)
                list.Add(new DashCheck("Sensores da CPU", SensorMonitor.Explain(sensors).Split('.')[0] + ".", false, "PageSensors", SensorMonitor.Explain(sensors)));
        }
        // Log de eventos: lido no máximo a cada 10 min (a consulta não é instantânea e o painel atualiza a cada minuto).
        if (_hwErrs == null || (DateTime.Now - _hwErrsAt).TotalMinutes >= 10)
        {
            _hwErrsAt = DateTime.Now;
            try { _hwErrs = OcCheck.ErrorsSince(DateTime.Now.AddDays(-7)); } catch { _hwErrs = new HardwareErrors(0, 0, null, "sem acesso"); }
        }
        var errs = _hwErrs;
        if (errs.Error == null && errs.Total > 0)
            list.Add(new DashCheck("Erros de hardware nos últimos 7 dias", $"{errs.Whea} WHEA · {errs.GpuResets} reinícios do driver de vídeo.", false, "PageOc",
                                   "Costuma ser overclock, XMP/EXPO instável ou driver. A página Overclock tem o verificador e o histórico."));
        try
        {
            var (reserved, inUse) = UsbCleaner.ComStatus();
            int stuck = reserved.Except(inUse).Count();
            // Alguns números reservados são normais (aparelhos desconectados); muitos indicam portas fantasma.
            if (stuck > 3) list.Add(new DashCheck("Portas COM presas", $"{stuck} números reservados por dispositivos que não estão conectados.", false, "PageMakerPorts",
                                                  "Placas Arduino/ESP32 novas acabam em COM20, COM30… Liberar os números não mexe nos aparelhos conectados."));
        }
        catch { }

        bool analyzed = _checks.Count > 0 && _secChecks.Count > 0;
        if (list.Count == 0)
            list.Add(new DashCheck(analyzed ? "Nada pendente" : "Analisando o PC…",
                                   analyzed ? "Desempenho, segurança, sensores e hardware sem pontos de atenção." : "O TURBO e o SHIELD rodam sozinhos logo depois de abrir.",
                                   analyzed));
        DashAction.Content = problems.Count > 0 ? "RESOLVER NO TURBO" : "ABRIR O TURBO";
        DashAction.Tag = problems.Any(p => p.page == "PageTurbo") || problems.Count == 0 ? "PageTurbo" : "PageShield";
        if (problems.Count > 0 && DashAction.Tag as string == "PageShield") DashAction.Content = "RESOLVER NO SHIELD";

        _health = list;
        DashHealth.ItemsSource = _health;
    }
}
