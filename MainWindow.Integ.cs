using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization;

/// <summary>Item da lista de dispositivos de iluminação: o que a linha mostra e o estado do efeito, sem alocar por quadro.</summary>
public sealed class LightDeviceVm : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public LightDevice Device { get; init; } = null!;
    public string Name => Device.Name;
    public string Detail => Device.Detail;
    public string Backend => Device.Backend.Name;
    public string LedsText => $"{Device.LedCount} LEDs";
    public bool Selected { get; set; } = true;
    /// <summary>Ordem em que o aparelho espera os canais. "GRB" = o vermelho do app vai no canal que o aparelho acende como verde.</summary>
    public string Order
    {
        get => _order;
        set
        {
            _order = value;
            Mapper = MapperFor(value);
            PropertyChanged?.Invoke(this, new(nameof(Order)));
            PropertyChanged?.Invoke(this, new(nameof(OrderText)));
            PropertyChanged?.Invoke(this, new(nameof(ConfidenceText)));
            PropertyChanged?.Invoke(this, new(nameof(ReasonText)));
            PropertyChanged?.Invoke(this, new(nameof(ColorTestVisibility)));
        }
    }
    private string _order = "RGB";
    /// <summary>Ordem usada quando o usuário não escolheu nenhuma.</summary>
    public string DefaultOrder { get; init; } = "RGB";
    public static string[] Orders { get; } = { "RGB", "GRB", "BRG", "RBG", "GBR", "BGR" };

    /// <summary>Resultado automático: guardado ao conectar, para mostrar sem resolver de novo.</summary>
    public LightOrderResolution? Automatic { get; init; }
    public bool IsManual => _order != DefaultOrder;
    public string OrderText => $"{_order} · {(IsManual ? "manual" : Automatic?.Source ?? "automática")}";
    /// <summary>Só quando a ordem automática não foi comprovada neste aparelho.</summary>
    public Visibility ColorTestVisibility => !IsManual && Automatic is { } a && ColorOrderAuto.NeedsColorTest(a.Confidence) ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Aparece só quando muda a decisão do usuário: confiança alta não polui a linha.</summary>
    public string ConfidenceText => IsManual ? "" : Automatic?.Confidence switch
    {
        DetectionConfidence.Medium => "confira a fita",
        DetectionConfidence.Low => "confiança baixa",
        DetectionConfidence.Unknown => "sem informação",
        _ => "",
    };
    public string ReasonText => IsManual ? "Escolhida à mão no modo Avançado." : Automatic?.Reason ?? "";

    private string _connection = "conectado";
    public string ConnectionText
    {
        get => _connection;
        set { if (_connection == value) return; _connection = value; PropertyChanged?.Invoke(this, new(nameof(ConnectionText))); }
    }

    /// <summary>Reordena os canais para a ordem do aparelho. Resolvido ao mudar a ordem: o efeito só chama, sem comparar texto a cada LED.</summary>
    public Func<(byte R, byte G, byte B), (byte, byte, byte)> Mapper { get; private set; } = LightChannelMapper.For(LightChannelOrder.RGB);

    public static Func<(byte R, byte G, byte B), (byte, byte, byte)> MapperFor(string order) =>
        LightChannelMapper.For(ColorOrderAuto.TryParseOrder(order, out var parsed) ? parsed : LightChannelOrder.RGB);

    // Estado do efeito: por aparelho, para o laço não procurar em dicionário nem alocar a cada quadro.
    internal (byte R, byte G, byte B)[] LastColors = Array.Empty<(byte, byte, byte)>();
    internal long LastSentAt;
    internal bool HasLast;
    internal int LastReportedError = int.MinValue;
    /// <summary>Criado uma vez: o envio avisa a janela só quando o resultado muda.</summary>
    internal Action<int>? SendDone;
}

/// <summary>
/// Aba INTEGRAÇÃO: controle de iluminação e efeitos. Pontes: Corsair iCUE (SDK), OpenRGB (MSI Mystic Light, Gigabyte RGB Fusion,
/// ASUS Aura, ASRock Polychrome e outras marcas), Razer Chroma e a Iluminação Dinâmica do Windows (LampArray). Todos os dispositivos aparecem numa lista só e recebem o mesmo efeito.
/// </summary>
public partial class MainWindow
{
    private CorsairSdk? _cue;
    private CorsairBackend? _corsair;
    private OpenRgbBackend? _orgb;
    private RazerChromaBackend? _razer;
    private DynamicLightingBackend? _dyn;
    private AulaHeBackend? _aula;
    /// <summary>Por que cada ponte não trouxe aparelhos (vai no aviso da lista quando ela fica vazia).</summary>
    private readonly Dictionary<string, string> _lightWhy = new();
    private readonly Dictionary<string, List<LightDevice>> _devsByBackend = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<LightDeviceVm> _lightVms = new();
    private DispatcherTimer? _lightTimer;
    private readonly LightPump _lightPump = new();
    private bool _lightRunning, _lightBusy, _lightInited;
    private string _effect = "static";
    private (byte R, byte G, byte B) _lightColor = (255, 255, 255);
    private readonly Stopwatch _lightClock = Stopwatch.StartNew();
    private float _smoothValue = float.NaN;
    private int _lastLightError;
    private readonly Dictionary<string, int> _lightErrByDevice = new();
    private readonly Queue<string> _diag = new();
    private (byte, byte, byte)[] _lightScratch = Array.Empty<(byte, byte, byte)>();
    private int _lightLiveShown = int.MinValue;

    /// <summary>Linha do painel de diagnóstico da aba (últimas 80 linhas).</summary>
    private void Diag(string line)
    {
        _diag.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_diag.Count > 80) _diag.Dequeue();
        if (LightDiag != null) { LightDiag.Text = string.Join("\n", _diag); LightDiag.ScrollToEnd(); }
    }

    private static readonly (string Name, byte R, byte G, byte B)[] Swatches =
    {
        ("Branco", 255, 255, 255), ("Vermelho", 255, 0, 0), ("Laranja", 255, 110, 0), ("Amarelo", 255, 210, 0),
        ("Verde", 0, 255, 60), ("Ciano", 0, 220, 255), ("Azul", 0, 80, 255), ("Roxo", 150, 0, 255), ("Rosa", 255, 0, 160),
    };

    private void InitInteg()
    {
        if (_lightInited) return;
        _lightInited = true;
        LightDevices.ItemsSource = _lightVms;
        foreach (var (name, r, g, b) in Swatches)
        {
            var btn = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name, BorderBrush = (Brush)FindResource("LineStrong"), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)).Frozen(),
                Tag = (r, g, b),
                Template = SwatchTemplate(),
            };
            btn.Click += (_, _) => { var (rr, gg, bb) = ((byte, byte, byte))btn.Tag; LightR.Value = rr; LightG.Value = gg; LightB.Value = bb; };
            LightSwatches.Children.Add(btn);
        }
        // Prioridade de fundo: o efeito nunca disputa com a interface; o envio é na thread do LightPump.
        _lightTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LightFrameInterval(_settings.LightMaxFps) };
        _lightTimer.Tick += LightTick;
        LightColor_Changed(null, null!);
        UpdateOrgbButton();
        _ = ConnectAllAsync(); // primeira abertura da aba: tenta conectar em tudo
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    // ---------------- Conexões ----------------

    private async Task ConnectAllAsync()
    {
        if (_lightBusy) return;
        _lightBusy = true;
        BtnLightConnect.IsEnabled = false;
        try
        {
            _lightWhy.Clear();
            await Task.WhenAll(ConnectCorsairAsync(), ConnectOrgbAsync(), ConnectRazerAsync(), ConnectDynAsync(), ConnectAulaAsync());
            RebuildLightList();
        }
        catch (Exception ex) { Diag("Falha geral: " + Err(ex)); }
        finally { _lightBusy = false; BtnLightConnect.IsEnabled = true; }
    }

    private void RebuildLightList()
    {
        var keep = _lightVms.Where(v => !v.Selected).Select(v => v.Device.Key).ToHashSet();
        _lightVms.Clear();
        foreach (var d in _devsByBackend.Values.SelectMany(x => x))
        {
            // Uma resolução por aparelho, ao conectar. O efeito só usa o mapper que sai daqui.
            var auto = ColorOrderAuto.Effective(d.Metadata, _settings.LightAutoOrder);
            string standard = auto.Order.ToString();
            _lightVms.Add(new LightDeviceVm
            {
                Device = d, Selected = !keep.Contains(d.Key), DefaultOrder = standard, Automatic = auto,
                Order = _settings.LightColorOrder.TryGetValue(d.Key, out var o) && LightDeviceVm.Orders.Contains(o) ? o : standard,
            });
        }
        LightDevicesHint.Text = _lightVms.Count == 0
            ? "Nenhum dispositivo encontrado. " + (_lightWhy.Count > 0 ? string.Join(" ", _lightWhy.Values) : "Conecte pelo menos uma integração acima.")
            : $"{_lightVms.Count} dispositivo(s), {_lightVms.Sum(v => v.Device.LedCount)} LEDs no total. Desligue o que não quer controlar.";
    }

    private async Task ConnectCorsairAsync()
    {
        try
        {
            if (_cue == null)
            {
                _cue = new CorsairSdk();
                _corsair = new CorsairBackend(_cue);
                _cue.StateChanged += s => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (s != 6 && _lightRunning) { StopLighting(false); LightStatus.Text = "O iCUE desconectou (" + CorsairSdk.StateText(s) + "). Clique em CONECTAR TUDO."; }
                }));
            }
            _devsByBackend.Remove(_corsair!.Name);
            string? dllProblem = _cue.LoadDll();
            BtnLightSdk.Visibility = dllProblem != null ? Visibility.Visible : Visibility.Collapsed;
            if (dllProblem != null)
            {
                _lightWhy["cue"] = _cue.DllRejected ? "iCUE: " + dllProblem
                                                    : "iCUE: SDK da Corsair não instalado (só precisa se você tem aparelhos Corsair).";
                LightStatus.Text = _cue.DllRejected ? dllProblem
                                   : "O SDK da Corsair (iCUESDK.x64_2019.dll) ainda não está instalado. Clique em BAIXAR O SDK DA CORSAIR: "
                                   + "o programa baixa só essa DLL do repositório oficial da Corsair e a instala para você.";
                return;
            }

            if (_cue.State != 6)
            {
                LightStatus.Text = "Conectando ao iCUE…";
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnState(int s) { if (s == 6 || s == 3 || s == 4) tcs.TrySetResult(s); }
                _cue.StateChanged += OnState;
                int err = _cue.Connect();
                if (err != 0 || _cue.State == 6) tcs.TrySetResult(_cue.State);
                await Task.WhenAny(tcs.Task, Task.Delay(6000));
                _cue.StateChanged -= OnState;
                if (_cue.State != 6)
                {
                    _lightWhy["cue"] = "iCUE: não respondeu (só precisa se você tem aparelhos Corsair).";
                    LightStatus.Text = "Não consegui conectar ao iCUE (" + (err != 0 ? CorsairSdk.ErrorText(err) : CorsairSdk.StateText(_cue.State)) + "). " +
                        "Abra o iCUE, vá em Configurações e ative o SDK (Enable SDK). Se o iCUE estiver em outra conta do Windows ou com permissão maior que a do tHg Optimization, a conexão também falha.";
                    return;
                }
            }

            string? prob = null;
            var devs = await Task.Run(() => _cue.ReadDevices(out prob));
            if (prob != null) { LightStatus.Text = prob; return; }
            _devsByBackend[_corsair.Name] = _corsair.Wrap(devs);

            Diag($"iCUE · DLL: {_cue.LoadedFrom} · sessão: {CorsairSdk.StateText(_cue.State)} · {devs.Count} dispositivo(s)");
            foreach (var d in devs)
            {
                string idShort = d.Id.Length > 14 ? d.Id[..14] + "…" : d.Id;
                string first = d.LedCount > 0 ? $"LED[0]=#{d.LedIds[0]} x={d.LedX[0]:0.00}" : "sem LEDs lidos";
                first += " · " + d.LedReadInfo;
                Diag($"  {d.TypeName} \"{d.Model}\" id={idShort} leds(ficha)={d.InfoLedCount} leds(posições)={d.LedCount} canais={d.ChannelCount} {first}");
            }
            LightStatus.Text = devs.Count == 0
                ? "Conectado ao iCUE, mas nenhum dispositivo foi listado. Confira se o iCUE LINK System Hub aparece no iCUE."
                : $"Conectado ao iCUE · {devs.Count} dispositivo(s), {devs.Sum(d => d.LedCount)} LEDs.";
        }
        catch (Exception ex) { LightStatus.Text = "Falhou: " + Err(ex); }
    }

    private async Task ConnectOrgbAsync()
    {
        try
        {
            _orgb ??= new OpenRgbBackend();
            _devsByBackend.Remove(_orgb.Name);
            UpdateRgbConflict();
            var (devs, status) = await Task.Run(() => _orgb.ConnectAsync("127.0.0.1", OrgbPort));
            // Sem servidor na porta: abre o OpenRGB instalado em modo servidor (sem administrador) e espera ele listar os aparelhos.
            if (devs.Count == 0 && _orgb.LastRefused && FindOpenRgb() == null)
            {
                _lightWhy["orgb"] = "OpenRGB: não está instalado (clique em BAIXAR E INSTALAR O OPENRGB para placa-mãe, memória e periféricos).";
                OrgbStatus.Text = "OpenRGB: não está instalado neste PC. Clique em BAIXAR E INSTALAR O OPENRGB: o tHg baixa a versão oficial " +
                                  "(codeberg.org/OpenRGB), instala na sua pasta de usuário e já liga o servidor, sem abrir o navegador.";
                Diag("OpenRGB · não instalado");
                return;
            }
            if (devs.Count == 0 && _orgb.LastRefused) (devs, status) = await StartOrgbServerAsync(status);
            if (devs.Count > 0)
            {
                _devsByBackend[_orgb.Name] = devs;
                OrgbStatus.Text = "OpenRGB: " + status;
            }
            else
            {
                _lightWhy["orgb"] = "OpenRGB: " + status;
                OrgbStatus.Text = "OpenRGB: " + status + " Ele cobre MSI Mystic Light, Gigabyte RGB Fusion, ASUS Aura e outras marcas: abra o OpenRGB e ligue o servidor " +
                                  "(aba SDK Server → Start Server), ou use o botão ao lado. Feche antes o MSI Center, o Armoury Crate e o RGB Fusion, que disputam os mesmos controladores.";
            }
            Diag("OpenRGB · " + status);
            foreach (var d in devs) Diag($"  {d.Name} · {d.Detail}");
        }
        catch (Exception ex) { OrgbStatus.Text = "OpenRGB: falhou: " + Err(ex); }
    }

    private async Task ConnectRazerAsync()
    {
        try
        {
            _razer ??= new RazerChromaBackend();
            _razer.EndSession();
            _devsByBackend.Remove(_razer.Name);
            var (devs, status) = await _razer.ConnectAsync();
            if (devs.Count > 0) _devsByBackend[_razer.Name] = devs;
            else _lightWhy["razer"] = "Razer: Synapse não encontrado (só precisa se você tem aparelhos Razer).";
            RazerStatus.Text = "Razer Chroma: " + status;
            Diag("Razer Chroma · " + status);
        }
        catch (Exception ex) { RazerStatus.Text = "Razer Chroma: falhou: " + Err(ex); }
    }

    private async Task ConnectDynAsync()
    {
        try
        {
            _dyn ??= new DynamicLightingBackend();
            _devsByBackend.Remove(_dyn.Name);
            var (devs, status) = await Task.Run(_dyn.ConnectAsync);
            if (devs.Count > 0) _devsByBackend[_dyn.Name] = devs;
            else _lightWhy["dyn"] = "Iluminação Dinâmica: " + status;
            DynStatus.Text = "Iluminação Dinâmica do Windows: " + status;
            Diag("Iluminação Dinâmica · " + status);
            foreach (var d in devs) Diag($"  {d.Name} · {d.Detail}");
        }
        catch (Exception ex) { DynStatus.Text = "Iluminação Dinâmica do Windows: falhou: " + Err(ex); }
    }

    // ---------------- Perfis do teclado AULA HE ----------------

    /// <summary>Processos (sem .exe) que pedem cada perfil. O cliente do LoL não conta: só a partida.</summary>
    private static readonly string[] AulaFpsGames = { "VALORANT-Win64-Shipping", "cs2" };
    private static readonly string[] AulaLolGames = { "League of Legends" };
    private DispatcherTimer? _aulaTimer;
    private int _aulaApplied = -1;

    private void StartAulaAuto()
    {
        _aula ??= new AulaHeBackend();
        AulaAuto.IsChecked = _settings.AulaAutoProfile;
        _aulaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        bool busy = false;
        _aulaTimer.Tick += async (_, _) =>
        {
            if (busy || !_settings.AulaAutoProfile) return;
            busy = true;
            try
            {
                // A cada 3 s: uma leitura da lista do kernel, sem criar um objeto Process por nome pedido (nunca liberados).
                int want = await Task.Run(() =>
                {
                    var running = Infrastructure.ProcessUtil.Snapshot();
                    return running.Any(p => AulaFpsGames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) ? _settings.AulaProfileFps
                         : running.Any(p => AulaLolGames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) ? _settings.AulaProfileLol : -1;
                });
                // Sem jogo aberto o perfil fica como está (o usuário pode ter escolhido outro à mão).
                if (want >= 0 && want != _aulaApplied) await ApplyAulaProfileAsync(want, auto: true);
            }
            catch { }
            finally { busy = false; }
        };
        _aulaTimer.Start();
    }

    private async Task ApplyAulaProfileAsync(int index, bool auto)
    {
        int ok = await Task.Run(() => _aula!.SetProfile(index));
        string text = ok > 0
            ? $"Perfil {index + 1} ativo no teclado AULA{(auto ? " (trocado pelo jogo aberto)" : "")}."
            : "Não achei o teclado AULA HE. Feche o driver web da AULA no navegador e confira o cabo.";
        if (ok > 0) _aulaApplied = index;
        AulaProfileStatus.Text = text;
        Diag("AULA HE · " + text);
        ResetLightCache(); // o perfil novo traz a luz dele: a iluminação ativa reenvia a cor
    }

    private async void AulaProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && int.TryParse(t, out int i)) await ApplyAulaProfileAsync(i, auto: false);
    }

    private void AulaAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (_aulaTimer == null) return; // ainda carregando
        _settings.AulaAutoProfile = AulaAuto.IsChecked == true;
        _settings.Save();
        _aulaApplied = -1;
    }

    private async Task ConnectAulaAsync()
    {
        try
        {
            _aula ??= new AulaHeBackend();
            _devsByBackend.Remove(_aula.Name);
            var (devs, status) = await Task.Run(_aula.ConnectAsync);
            if (devs.Count > 0) _devsByBackend[_aula.Name] = devs;
            Diag("AULA HE · " + status);
        }
        catch (Exception ex) { Diag("AULA HE · falhou: " + Err(ex)); }
    }

    private void BtnDynSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true }); } catch { }
    }

    private async void BtnLightConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_lightRunning) StopLighting(true);
        await ConnectAllAsync();
    }

    private const int OrgbPort = 6742;

    /// <summary>
    /// Abre o OpenRGB.exe encontrado com --server na porta 6742 (como administrador, ver StartOrgb), e tenta conectar
    /// até ele responder com a lista de aparelhos. A detecção do OpenRGB leva alguns segundos: o socket abre antes e responde
    /// com menos controladores até terminar, então espera a contagem parar de subir.
    /// </summary>
    private async Task<(List<LightDevice> Devices, string Status)> StartOrgbServerAsync(string firstStatus)
    {
        if (OrgbRunning())
        {
            Diag("OpenRGB · já está aberto, mas sem o servidor SDK na porta " + OrgbPort);
            return (new List<LightDevice>(), $"o OpenRGB está aberto, mas sem o servidor SDK ligado. No OpenRGB: aba SDK Server → Start Server (porta {OrgbPort}).");
        }
        string? exe = FindOpenRgb();
        if (exe == null) return (new List<LightDevice>(), firstStatus + " Não achei o OpenRGB.exe instalado.");
        if (RgbConflicts() is { Count: > 0 } busy)
            Diag("OpenRGB · aviso: " + string.Join(", ", busy) + " aberto(s); os aparelhos USB que ele(s) segura(m) não vão aparecer");
        try
        {
            OrgbStatus.Text = "OpenRGB: servidor desligado. Abrindo " + exe + " em modo servidor…";
            Diag($"OpenRGB · iniciando {exe} --server --server-port {OrgbPort} --startminimized");
            StartOrgb(exe, $"--server --server-port {OrgbPort} --startminimized");
        }
        catch (Exception ex) { return (new List<LightDevice>(), firstStatus + " Não consegui abrir o OpenRGB: " + Err(ex)); }

        var result = (Devices: new List<LightDevice>(), Status: firstStatus);
        int lastCount = -1;
        for (int i = 0; i < 20; i++) // até ~40 s
        {
            await Task.Delay(2000);
            result = await Task.Run(() => _orgb!.ConnectAsync("127.0.0.1", OrgbPort));
            if (_orgb!.LastRefused) { OrgbStatus.Text = $"OpenRGB: esperando o servidor abrir ({(i + 1) * 2} s)…"; continue; }
            OrgbStatus.Text = $"OpenRGB: servidor aberto, detectando aparelhos ({result.Devices.Count})…";
            if (result.Devices.Count > 0 && result.Devices.Count == lastCount) break; // a contagem parou de subir
            lastCount = result.Devices.Count;
        }
        if (_orgb!.LastRefused) return (result.Devices, $"abri o OpenRGB, mas o servidor não respondeu na porta {OrgbPort} em 40 s.");
        return result;
    }

    /// <summary>
    /// Exceção à regra de abrir programas sem administrador (Unelevated): o OpenRGB precisa do privilégio do tHg para o
    /// driver PawnIO abrir o SMBus. Sem isso o log dele mostra "Permission Denied" e somem as memórias RGB, o RGB de
    /// algumas placas-mãe e o de placas de vídeo.
    /// </summary>
    private static void StartOrgb(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });

    private static bool OrgbRunning() => Infrastructure.ProcessUtil.Snapshot().Any(p => p.Name.Equals("OpenRGB", StringComparison.OrdinalIgnoreCase));

    /// <summary>SignalRGB e L-Connect 3 seguram os controladores USB HID; enquanto rodam, o OpenRGB não consegue falar com eles.</summary>
    private static bool IsRgbConflict(string name) =>
        name.StartsWith("SignalRgb", StringComparison.OrdinalIgnoreCase)
        || name.Replace(" ", "").StartsWith("L-Connect", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("LConnect", StringComparison.OrdinalIgnoreCase);

    private static List<string> RgbConflicts()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (IsRgbConflict(p.ProcessName)) found.Add(p.ProcessName.StartsWith("SignalRgb", StringComparison.OrdinalIgnoreCase) ? "SignalRGB" : "L-Connect 3"); }
            catch { }
            finally { p.Dispose(); }
        }
        return found.ToList();
    }

    private void UpdateRgbConflict()
    {
        var c = RgbConflicts();
        RgbConflictBox.Visibility = c.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (c.Count == 0) return;
        RgbConflictText.Text = $"O {string.Join(" e o ", c)} está aberto e segura os controladores USB (HID) de iluminação. Enquanto ele roda, " +
                               "o servidor do OpenRGB não consegue controlar esses aparelhos (eles somem da lista ou não mudam de cor).";
        BtnRgbConflict.Content = c.Contains("SignalRGB") ? "Fechar SignalRGB para liberar os dispositivos" : "Fechar L-Connect 3 para liberar os dispositivos";
    }

    private async void BtnRgbConflict_Click(object sender, RoutedEventArgs e)
    {
        BtnRgbConflict.IsEnabled = false;
        try
        {
            var closed = await Task.Run(() =>
            {
                var names = new List<string>();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (!IsRgbConflict(p.ProcessName)) continue;
                        // Pede para fechar; quem só vive na bandeja (SignalRGB) não tem janela e é encerrado.
                        if (!(p.CloseMainWindow() && p.WaitForExit(3000))) { p.Kill(true); p.WaitForExit(3000); }
                        names.Add(p.ProcessName);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                return names;
            });
            Diag(closed.Count > 0 ? "Fechados para liberar o USB: " + string.Join(", ", closed.Distinct()) : "Nada foi fechado (já tinha saído ou o Windows negou)");
            await Task.Delay(1500); // o Windows solta os aparelhos HID logo depois que o processo sai
            UpdateRgbConflict();
            if (_lightRunning) StopLighting(true);
            await ConnectAllAsync();
        }
        finally { BtnRgbConflict.IsEnabled = true; }
    }

    internal static string? FindOpenRgb()
    {
        try
        {
            var dirs = new List<string>
            {
                AppContext.BaseDirectory,
                Path.Combine(AppContext.BaseDirectory, "OpenRGB"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRGB"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB"),
            };
            foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
                                            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (var sub in Directory.GetDirectories(baseDir, "OpenRGB*"))
                {
                    dirs.Add(sub);
                    dirs.AddRange(Directory.GetDirectories(sub));
                }
            }
            foreach (var d in dirs)
            {
                string exe = Path.Combine(d, "OpenRGB.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return null;
    }

    private void UpdateOrgbButton() =>
        BtnOrgbOpen.Content = FindOpenRgb() != null ? "ABRIR O OPENRGB" : "BAIXAR E INSTALAR O OPENRGB";

    private async void BtnOrgbOpen_Click(object sender, RoutedEventArgs e)
    {
        string? exe = FindOpenRgb();
        if (exe == null)
        {
            if (_lightBusy) return;
            _lightBusy = true;
            BtnOrgbOpen.IsEnabled = false;
            BtnLightConnect.IsEnabled = false;
            try
            {
                exe = await OpenRgbInstaller.InstallAsync(t => Dispatcher.BeginInvoke(new Action(() => OrgbStatus.Text = "OpenRGB: " + t)));
                Diag("OpenRGB · instalado em " + exe);
                OrgbStatus.Text = "OpenRGB: instalado em " + Path.GetDirectoryName(exe) + ". Ligando o servidor e procurando os aparelhos…";
            }
            catch (Exception ex) { OrgbStatus.Text = "OpenRGB: não consegui baixar/instalar: " + Err(ex); return; }
            finally
            {
                _lightBusy = false;
                BtnOrgbOpen.IsEnabled = true;
                BtnLightConnect.IsEnabled = true;
                UpdateOrgbButton();
            }
            if (_lightRunning) StopLighting(true);
            await ConnectAllAsync(); // abre o OpenRGB recém-instalado em modo servidor e espera a lista
            return;
        }
        try
        {
            StartOrgb(exe, OrgbRunning() ? "" : $"--server --server-port {OrgbPort} --startminimized");
            OrgbStatus.Text = "OpenRGB: abrindo o servidor…";
            await Task.Delay(4000);
            await ConnectAllAsync();
        }
        catch (Exception ex) { OrgbStatus.Text = "OpenRGB: não consegui abrir: " + Err(ex); }
    }

    private async void BtnLightSdk_Click(object sender, RoutedEventArgs e)
    {
        if (_lightBusy) return;
        var ok = ThgDialog.Show(this,
            "O tHg Optimization vai baixar o SDK oficial da Corsair (github.com/CorsairOfficial/cue-sdk), extrair somente o arquivo " +
            "iCUESDK.x64_2019.dll para a pasta\n" + CorsairSdkInstaller.TargetPath + "\ne conferir a assinatura digital dele.\n\nContinuar?",
            "tHg Optimization · SDK da Corsair", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        _lightBusy = true;
        BtnLightSdk.IsEnabled = false;
        BtnLightConnect.IsEnabled = false;
        try
        {
            string msg = await CorsairSdkInstaller.InstallAsync(t => Dispatcher.BeginInvoke(new Action(() => LightStatus.Text = t)));
            LightStatus.Text = msg;
            BtnLightSdk.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { LightStatus.Text = "Não foi possível instalar o SDK: " + Err(ex); return; }
        finally
        {
            _lightBusy = false;
            BtnLightSdk.IsEnabled = true;
            BtnLightConnect.IsEnabled = true;
        }
        await ConnectAllAsync();
    }

    // ---------------- Controle ----------------

    private void BtnLightRun_Click(object sender, RoutedEventArgs e)
    {
        if (_lightRunning) { StopLighting(true); return; }
        var sel = _lightVms.Where(v => v.Selected && v.Device.LedCount > 0).ToList();
        if (sel.Count == 0) { LightRunHint.Text = "Conecte e marque pelo menos um dispositivo com LEDs."; return; }
        LightRunHint.Text = "";

        var problems = new List<string>();
        bool exclusive = LightExclusive.IsChecked == true;
        if (_cue != null && _cue.State == 6 && sel.Any(v => v.Device.Backend == _corsair))
        {
            int pr = _cue.SetLayerPriority(255);
            Diag($"iCUE · prioridade da camada 255: {(pr == -1 ? "função ausente nesta versão do SDK" : CorsairSdk.ErrorText(pr))} · modo {(exclusive ? "exclusivo" : "compartilhado")}");
        }
        _lightErrByDevice.Clear();
        foreach (var v in sel)
        {
            int err = v.Device.Backend.Acquire(v.Device, exclusive);
            string note = v.Device.Backend is OpenRgbBackend ob ? " · " + ob.ModeNote(v.Device) : "";
            Diag($"Pedido de controle de \"{v.Name}\" ({v.Device.Backend.Name}): {v.Device.Backend.ErrorText(err)}{note}");
            if (err != 0) problems.Add($"{v.Name}: {v.Device.Backend.ErrorText(err)}");
        }
        ResetLightCache();
        _smoothValue = float.NaN;
        _lightRunning = true;
        BtnLightRun.Content = "DEVOLVER AO ORIGINAL";
        LightRunHint.Text = problems.Count == 0
            ? $"Controlando {sel.Count} dispositivo(s)."
            : "Controle ativo, mas com avisos: " + string.Join("; ", problems);
        _lightTimer!.Start();
    }

    private void StopLighting(bool release)
    {
        _lightTimer?.Stop();
        _lightRunning = false;
        BtnLightRun.Content = "ATIVAR CONTROLE";
        LightLive.Text = "";
        if (!release) return;
        // Antes de devolver: um quadro atrasado depois do Release tomaria as luzes de volta do fabricante.
        if (!_lightPump.Drain(TimeSpan.FromMilliseconds(250))) Diag("Um aparelho não terminou o envio a tempo: o controle foi devolvido mesmo assim.");
        var backends = new HashSet<ILightBackend>();
        foreach (var v in _lightVms.Where(v => v.Selected))
        {
            try { v.Device.Backend.Release(v.Device); backends.Add(v.Device.Backend); } catch { }
        }
        foreach (var b in backends) { try { b.EndSession(); } catch { } }
        LightRunHint.Text = "Controle devolvido: o iCUE, o OpenRGB, o Synapse e o Windows reassumem as luzes.";
    }

    private void ShutdownInteg()
    {
        try { StopLighting(true); } catch (Exception ex) { Infrastructure.AppLog.Error("rgb", ex); }
        // Primeiro o envio, depois as pontes. Fechar o socket também destrava um envio preso, e a segunda espera confirma o fim da thread.
        bool stopped = _lightPump.Shutdown(TimeSpan.FromMilliseconds(500));
        try { _cue?.Dispose(); } catch (Exception ex) { Infrastructure.AppLog.Error("rgb", ex); }
        try { _orgb?.Shutdown(); } catch (Exception ex) { Infrastructure.AppLog.Error("rgb", ex); }
        try { _razer?.Shutdown(); } catch (Exception ex) { Infrastructure.AppLog.Error("rgb", ex); }
        try { _aula?.Shutdown(); } catch (Exception ex) { Infrastructure.AppLog.Error("rgb", ex); }
        if (!stopped) _lightPump.Shutdown(TimeSpan.FromMilliseconds(500));
    }

    private void LightOrder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LightDeviceVm vm } || e.AddedItems.Count == 0) return;
        // Este evento também dispara ao montar a lista: sem mudança, nada a salvar. Igual ao padrão não é escolha do usuário.
        bool saved = _settings.LightColorOrder.TryGetValue(vm.Device.Key, out var previous);
        if (vm.Order == vm.DefaultOrder ? !saved : previous == vm.Order) return;
        if (vm.Order == vm.DefaultOrder) _settings.LightColorOrder.Remove(vm.Device.Key);
        else _settings.LightColorOrder[vm.Device.Key] = vm.Order;
        _settings.Save();
        ResetLightCache();
        vm.LastReportedError = int.MinValue;
        _lightErrByDevice.Remove(vm.Device.Key);
        Diag($"Ordem das cores de \"{vm.Name}\": {vm.Order}");
    }

    private void LightFx_Checked(object sender, RoutedEventArgs e)
    {
        if (LightLive == null) return;
        _effect = (string)((FrameworkElement)sender).Tag;
        _smoothValue = float.NaN;
        ResetLightCache();
    }

    /// <summary>A cor ou a ordem mudou: todo aparelho recebe de novo no próximo quadro.</summary>
    private void ResetLightCache()
    {
        foreach (var vm in _lightVms) vm.HasLast = false;
    }

    private LightDeviceVm? _lightTesting;

    /// <summary>Acende vermelho, verde e azul, 1 s cada, no aparelho. O efeito em andamento pula este aparelho enquanto isso.</summary>
    private async void LightTestColors_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LightDeviceVm vm } || _lightTesting != null || vm.Device.LedCount == 0) return;
        _lightTesting = vm;
        string before = vm.ConnectionText;
        try
        {
            var buffer = new (byte R, byte G, byte B)[vm.Device.LedCount];
            foreach (var (name, color) in ColorOrderAuto.TestSequence)
            {
                vm.ConnectionText = "teste: " + name;
                Array.Fill(buffer, vm.Mapper(color));
                _lightPump.Post(vm.Device, buffer, vm.SendDone ??= SendDoneFor(vm));
                await Task.Delay(1000);
            }
            LightRunHint.Text = $"{vm.Name}: viu vermelho, verde e azul, nesta ordem? Então está certo. Se não, escolha outra ordem no modo Avançado.";
        }
        catch (Exception ex)
        {
            Infrastructure.AppLog.Error("rgb", ex);
            LightRunHint.Text = $"{vm.Name}: não foi possível testar as cores ({ex.Message}).";
        }
        finally
        {
            _lightTesting = null;
            vm.ConnectionText = before;
            vm.HasLast = false; // o efeito em andamento reenvia a cor dele
            if (!_lightRunning && vm.LastColors.Length == vm.Device.LedCount) _lightPump.Post(vm.Device, vm.LastColors, vm.SendDone ??= SendDoneFor(vm));
        }
    }

    private void LightAutoOrder_Click(object sender, RoutedEventArgs e)
    {
        _settings.LightAutoOrder = ChkLightAutoOrder.IsChecked == true;
        _settings.Save();
        RebuildLightList();
        ResetLightCache();
        Diag(_settings.LightAutoOrder ? "Ordem das cores: regras por fabricante ligadas." : "Ordem das cores: regras por fabricante desligadas.");
    }

    private void LightAdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        bool open = LightAdvancedToggle.IsChecked == true;
        ChkLightAutoOrder.IsChecked = _settings.LightAutoOrder;
        LightAdvancedPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!open) return;
        LightOrderOverrides.ItemsSource ??= _lightVms; // só monta os controles de quem abriu
        if (LightFpsCombo.ItemsSource == null)
        {
            LightFpsCombo.ItemsSource = LightFpsChoices;
            LightFpsCombo.SelectedItem = Array.IndexOf(LightFpsChoices, _settings.LightMaxFps) >= 0 ? _settings.LightMaxFps : 30;
        }
    }

    private static readonly int[] LightFpsChoices = { 10, 15, 20, 30 };

    private void LightFps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LightFpsCombo.SelectedItem is not int fps || fps == _settings.LightMaxFps) return;
        _settings.LightMaxFps = fps;
        _settings.Save();
        if (_lightTimer != null) _lightTimer.Interval = LightFrameInterval(fps);
    }

    /// <summary>Teto de 30 atualizações por segundo, mesmo com um valor maior no arquivo de configurações.</summary>
    internal static TimeSpan LightFrameInterval(int maxFps) => TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(maxFps, 1, 30));

    private void LightColor_Changed(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LightLive == null) return;
        _lightColor = ((byte)LightR.Value, (byte)LightG.Value, (byte)LightB.Value);
        LightPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(_lightColor.R, _lightColor.G, _lightColor.B)).Frozen();
        LightBrightText.Text = $"{LightBright.Value:0}%";
        LightSpeedText.Text = $"{LightSpeed.Value:0}";
        ResetLightCache();
    }

    // ---------------- Efeitos ----------------

    private static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static (byte, byte, byte) Dim((byte R, byte G, byte B) c, double bright) =>
        ((byte)(c.R * bright), (byte)(c.G * bright), (byte)(c.B * bright));

    private float? GpuTemperature() => _monitor.GpuCoreTemperature;

    /// <summary>Valor ao vivo que dirige o efeito (temperatura ou carga), já suavizado. Null se o sensor não responde.</summary>
    private float? LiveValue()
    {
        float? raw = _effect switch
        {
            "cpu" => _monitor.CpuTemperature,
            "gpu" => GpuTemperature(),
            "load" => _monitor.CpuLoad,
            _ => null
        };
        if (raw == null) return null;
        _smoothValue = float.IsNaN(_smoothValue) ? raw.Value : _smoothValue + (raw.Value - _smoothValue) * 0.08f;
        return _smoothValue;
    }

    private void LightTick(object? sender, EventArgs e)
    {
        double t = _lightClock.Elapsed.TotalSeconds;
        double bright = LightBright.Value / 100.0;
        double speed = LightSpeed.Value;
        var (cr, cg, cb) = _lightColor;

        (byte, byte, byte)? live = null;
        if (_effect is "cpu" or "gpu" or "load")
        {
            float? v = LiveValue();
            if (v == null) { _lightLiveShown = int.MinValue; LightLive.Text = "Sensor indisponível."; live = (0, 0, 40); }
            else
            {
                double hue;
                // Número inteiro e janela visível: a mesma leitura não monta texto nem toca no layout de novo.
                int shown = (int)Math.Round(v.Value);
                bool label = shown != _lightLiveShown && IsVisible;
                if (label) _lightLiveShown = shown;
                if (_effect == "load") { hue = 120 * (1 - Math.Clamp(v.Value / 100.0, 0, 1)); if (label) LightLive.Text = $"CPU {shown}% de carga"; }
                else { hue = 240 * (1 - Math.Clamp((v.Value - 35) / 50.0, 0, 1)); if (label) LightLive.Text = $"{(_effect == "cpu" ? "CPU" : "GPU")} {shown} °C"; }
                live = Hsv(hue, 1, 1);
            }
        }
        else if (LightLive.Text.Length > 0) { _lightLiveShown = int.MinValue; LightLive.Text = ""; }

        // Uma cor para todos os LEDs nos efeitos sem variação: o cálculo sai do laço de LEDs. Só o arco-íris muda a cada LED.
        bool rainbow = _effect == "rainbow";
        (byte R, byte G, byte B) solid = (cr, cg, cb);
        switch (_effect)
        {
            case "breath":
                double k = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (0.1 * speed) * t);
                k = 0.04 + 0.96 * k;
                solid = ((byte)(cr * k), (byte)(cg * k), (byte)(cb * k));
                break;
            case "cpu": case "gpu": case "load":
                solid = live ?? (0, 0, 0);
                break;
            case "off":
                solid = (0, 0, 0);
                break;
        }
        solid = Dim(solid, bright);

        long now = Environment.TickCount64;
        for (int n = 0; n < _lightVms.Count; n++)
        {
            var vm = _lightVms[n];
            if (!vm.Selected || vm.Device.LedCount == 0 || ReferenceEquals(vm, _lightTesting)) continue;
            var d = vm.Device;
            if (d.Backend == _corsair && (_cue == null || _cue.State != 6)) continue;

            if (vm.HasLast && now - vm.LastSentAt < d.Backend.MinIntervalMs) continue;

            // Roda até 30 vezes por segundo: calcula num buffer reaproveitado e não aloca para enviar.
            if (_lightScratch.Length < d.LedCount) _lightScratch = new (byte, byte, byte)[d.LedCount];
            var scratch = _lightScratch.AsSpan(0, d.LedCount);
            var mapper = vm.Mapper; // resolvido uma vez por aparelho, fora do laço de LEDs
            if (rainbow)
            {
                for (int i = 0; i < scratch.Length; i++)
                    scratch[i] = mapper(Dim(Hsv(d.LedX[i] * 360 + t * speed * 36, 1, 1), bright));
            }
            else scratch.Fill(mapper(solid));

            // só reenvia se mudou (ou a cada 2 s, para o fabricante não retomar as luzes)
            if (vm.HasLast && now - vm.LastSentAt < 2000 && vm.LastColors.AsSpan().SequenceEqual(scratch)) continue;
            if (vm.LastColors.Length != scratch.Length) vm.LastColors = new (byte, byte, byte)[scratch.Length];
            scratch.CopyTo(vm.LastColors);
            vm.LastSentAt = now;
            vm.HasLast = true;
            _lightPump.Post(d, scratch, vm.SendDone ??= SendDoneFor(vm));
        }
    }

    /// <summary>Roda na thread de envio. Só incomoda a janela quando o resultado muda, e não a cada quadro de cada aparelho.</summary>
    private Action<int> SendDoneFor(LightDeviceVm vm) => err =>
    {
        if (vm.LastReportedError == err) return;
        vm.LastReportedError = err;
        Dispatcher.BeginInvoke(() =>
        {
            var d = vm.Device;
            string name = vm.Name + (vm.Order == "RGB" ? "" : $" [ordem {vm.Order}]");
            var first = vm.LastColors.Length > 0 ? vm.LastColors[0] : default;
            _lightErrByDevice[d.Key] = err;
            Diag($"Cor ({_lightColor.R},{_lightColor.G},{_lightColor.B}) enviada a \"{name}\" ({d.Backend.Name}, {d.LedCount} LEDs, primeiro = {first.R},{first.G},{first.B}): {d.Backend.ErrorText(err)}");
            vm.ConnectionText = err == 0 ? "conectado" : "falha no envio";
            if (err != 0 && err != _lastLightError) LightRunHint.Text = $"{name}: {d.Backend.ErrorText(err)}";
            _lastLightError = err;
        });
    };
}
