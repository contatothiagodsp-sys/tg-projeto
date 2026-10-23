using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization;

/// <summary>Item da lista de dispositivos de iluminação (caixa de seleção + nome).</summary>
public sealed class LightDeviceVm
{
    public LightDevice Device { get; init; } = null!;
    public string Name => Device.Name;
    public string Detail => Device.Detail;
    public bool Selected { get; set; } = true;
    /// <summary>Ordem em que o aparelho espera os canais. "GRB" = o vermelho do app vai no canal que o aparelho acende como verde.</summary>
    public string Order { get; set; } = "RGB";
    public static string[] Orders { get; } = { "RGB", "GRB", "BRG", "RBG", "GBR", "BGR" };

    /// <summary>Reordena os canais para a ordem do aparelho. Vale para todos os dispositivos, de qualquer ponte.</summary>
    public static (byte, byte, byte) Map((byte R, byte G, byte B) c, string order) => order switch
    {
        "GRB" => (c.G, c.R, c.B), "BRG" => (c.B, c.R, c.G), "RBG" => (c.R, c.B, c.G),
        "GBR" => (c.G, c.B, c.R), "BGR" => (c.B, c.G, c.R), _ => c,
    };
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
    /// <summary>Por que cada ponte não trouxe aparelhos (vai no aviso da lista quando ela fica vazia).</summary>
    private readonly Dictionary<string, string> _lightWhy = new();
    private readonly Dictionary<string, List<LightDevice>> _devsByBackend = new();
    private List<LightDeviceVm> _lightVms = new();
    private DispatcherTimer? _lightTimer;
    private readonly LightPump _lightPump = new();
    private bool _lightRunning, _lightBusy, _lightInited;
    private string _effect = "static";
    private (byte R, byte G, byte B) _lightColor = (255, 255, 255);
    private readonly Stopwatch _lightClock = Stopwatch.StartNew();
    private readonly Dictionary<string, ((byte, byte, byte)[] Colors, long At)> _lightLast = new();
    private float _smoothValue = float.NaN;
    private int _lastLightError;
    private readonly Dictionary<string, int> _lightErrByDevice = new();
    private readonly Queue<string> _diag = new();

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
        foreach (var (name, r, g, b) in Swatches)
        {
            var btn = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name, BorderBrush = (Brush)FindResource("LineStrong"), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)),
                Tag = (r, g, b),
                Template = SwatchTemplate(),
            };
            btn.Click += (_, _) => { var (rr, gg, bb) = ((byte, byte, byte))btn.Tag; LightR.Value = rr; LightG.Value = gg; LightB.Value = bb; };
            LightSwatches.Children.Add(btn);
        }
        // Prioridade de fundo: o efeito nunca disputa com a interface; o envio é na thread do LightPump.
        _lightTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
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
            await Task.WhenAll(ConnectCorsairAsync(), ConnectOrgbAsync(), ConnectRazerAsync(), ConnectDynAsync());
            RebuildLightList();
        }
        catch (Exception ex) { Diag("Falha geral: " + Err(ex)); }
        finally { _lightBusy = false; BtnLightConnect.IsEnabled = true; }
    }

    private void RebuildLightList()
    {
        var keep = _lightVms.Where(v => !v.Selected).Select(v => v.Device.Key).ToHashSet();
        _lightVms = _devsByBackend.Values.SelectMany(x => x)
            .Select(d => new LightDeviceVm
            {
                Device = d, Selected = !keep.Contains(d.Key),
                Order = _settings.LightColorOrder.TryGetValue(d.Key, out var o) && LightDeviceVm.Orders.Contains(o) ? o : "RGB",
            }).ToList();
        LightDevices.ItemsSource = _lightVms;
        LightDevicesHint.Text = _lightVms.Count == 0
            ? "Nenhum dispositivo encontrado. " + (_lightWhy.Count > 0 ? string.Join(" ", _lightWhy.Values) : "Conecte pelo menos uma integração acima.")
            : $"{_lightVms.Count} dispositivo(s), {_lightVms.Sum(v => v.Device.LedCount)} LEDs no total. Desmarque o que não quer controlar.";
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
                _lightWhy["cue"] = "iCUE: SDK da Corsair não instalado (só precisa se você tem aparelhos Corsair).";
                LightStatus.Text = "O SDK da Corsair (iCUESDK.x64_2019.dll) ainda não está instalado. Clique em BAIXAR O SDK DA CORSAIR: "
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

    private static bool OrgbRunning() => Process.GetProcesses().Any(p =>
    {
        try { return p.ProcessName.Equals("OpenRGB", StringComparison.OrdinalIgnoreCase); } catch { return false; } finally { p.Dispose(); }
    });

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

    private static string? FindOpenRgb()
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
        _lightLast.Clear();
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
        try { StopLighting(true); } catch { }
        try { _cue?.Dispose(); } catch { }
        try { _orgb?.Shutdown(); } catch { }
        try { _razer?.Shutdown(); } catch { }
    }

    private void LightOrder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LightDeviceVm vm } || e.AddedItems.Count == 0) return;
        if (vm.Order == "RGB") _settings.LightColorOrder.Remove(vm.Device.Key);
        else _settings.LightColorOrder[vm.Device.Key] = vm.Order;
        _settings.Save();
        _lightLast.Clear();
        _lightErrByDevice.Remove(vm.Device.Key);
        Diag($"Ordem das cores de \"{vm.Name}\": {vm.Order}");
    }

    private void LightFx_Checked(object sender, RoutedEventArgs e)
    {
        if (LightLive == null) return;
        _effect = (string)((FrameworkElement)sender).Tag;
        _smoothValue = float.NaN;
        _lightLast.Clear();
    }

    private void LightColor_Changed(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LightLive == null) return;
        _lightColor = ((byte)LightR.Value, (byte)LightG.Value, (byte)LightB.Value);
        LightPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(_lightColor.R, _lightColor.G, _lightColor.B));
        LightBrightText.Text = $"{LightBright.Value:0}%";
        LightSpeedText.Text = $"{LightSpeed.Value:0}";
        _lightLast.Clear();
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

    private float? GpuTemperature()
    {
        var r = _monitor.Rows.Where(x => (x.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                                         && x.Type == SensorType.Temperature && x.RawValue is > 0).ToList();
        return (r.FirstOrDefault(x => x.Name.Contains("Core")) ?? r.FirstOrDefault())?.RawValue;
    }

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
            if (v == null) { LightLive.Text = "Sensor indisponível."; live = (0, 0, 40); }
            else
            {
                double hue;
                if (_effect == "load") { hue = 120 * (1 - Math.Clamp(v.Value / 100.0, 0, 1)); LightLive.Text = $"CPU {v:0}% de carga"; }
                else { hue = 240 * (1 - Math.Clamp((v.Value - 35) / 50.0, 0, 1)); LightLive.Text = $"{(_effect == "cpu" ? "CPU" : "GPU")} {v:0} °C"; }
                live = Hsv(hue, 1, 1);
            }
        }
        else LightLive.Text = "";

        long now = Environment.TickCount64;
        foreach (var vm in _lightVms)
        {
            if (!vm.Selected || vm.Device.LedCount == 0) continue;
            var d = vm.Device;
            if (d.Backend == _corsair && (_cue == null || _cue.State != 6)) continue;

            bool have = _lightLast.TryGetValue(d.Key, out var last);
            if (have && now - last.At < d.Backend.MinIntervalMs) continue;

            var cols = new (byte, byte, byte)[d.LedCount];
            for (int i = 0; i < cols.Length; i++)
            {
                (byte R, byte G, byte B) c;
                switch (_effect)
                {
                    case "breath":
                        double k = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (0.1 * speed) * t);
                        k = 0.04 + 0.96 * k;
                        c = ((byte)(cr * k), (byte)(cg * k), (byte)(cb * k));
                        break;
                    case "rainbow":
                        c = Hsv(d.LedX[i] * 360 + t * speed * 36, 1, 1);
                        break;
                    case "cpu": case "gpu": case "load":
                        c = live ?? (0, 0, 0);
                        break;
                    case "off":
                        c = (0, 0, 0);
                        break;
                    default:
                        c = (cr, cg, cb);
                        break;
                }
                cols[i] = LightDeviceVm.Map(((byte)(c.R * bright), (byte)(c.G * bright), (byte)(c.B * bright)), vm.Order);
            }

            // só reenvia se mudou (ou a cada 2 s, para o fabricante não retomar as luzes)
            if (have && now - last.At < 2000 && last.Colors.AsSpan().SequenceEqual(cols)) continue;
            _lightLast[d.Key] = (cols, now);
            string vmName = vm.Name + (vm.Order == "RGB" ? "" : $" [ordem {vm.Order}]");
            _lightPump.Post(d, cols, err => Dispatcher.BeginInvoke(() =>
            {
                if (!_lightErrByDevice.TryGetValue(d.Key, out int prevErr) || prevErr != err)
                {
                    _lightErrByDevice[d.Key] = err;
                    Diag($"Cor ({_lightColor.R},{_lightColor.G},{_lightColor.B}) enviada a \"{vmName}\" ({d.Backend.Name}, {cols.Length} LEDs, primeiro = {cols[0].Item1},{cols[0].Item2},{cols[0].Item3}): {d.Backend.ErrorText(err)}");
                }
                if (err != 0 && err != _lastLightError) LightRunHint.Text = $"{vmName}: {d.Backend.ErrorText(err)}";
                _lastLightError = err;
            }));
        }
    }
}
