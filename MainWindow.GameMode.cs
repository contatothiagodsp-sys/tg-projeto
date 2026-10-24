using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace ThgOptimization;

/// <summary>Linha da lista de sessões (texto à esquerda e números à direita).</summary>
public sealed record SessionRow(string Item1, string Item2);

/// <summary>Linha da lista de perfis do driver.</summary>
public sealed class ProRow : INotifyPropertyChanged
{
    public GamePreset Preset { get; init; } = null!;
    public string Name => Preset.Name;
    public string Kind => Preset.Kind;
    public bool Installed { get; init; }
    private string _recipe = "";
    public string Recipe { get => _recipe; set { _recipe = value; Changed(nameof(Recipe)); } }
    private string _state = "";
    public string State { get => _state; set { _state = value; Changed(nameof(State)); } }
    private bool _selected;
    public bool Selected { get => _selected; set { _selected = value; Changed(nameof(Selected)); } }
    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>GAME · Modo Jogo: automático, perfis pro no driver, cache de shaders e antes/depois do TURBO.</summary>
public partial class MainWindow
{
    private GameModeEngine? _gm;
    private DispatcherTimer? _gmTimer;
    private List<ProRow> _proRows = new();
    private bool _gmPageInit;

    /// <summary>Liga o motor do Modo Jogo na abertura do app (depois que o hardware e a biblioteca foram lidos).</summary>
    private async void StartGameModeEngine()
    {
        if (_gm != null) return;
        Notifications.Restore(_settings); // o tHg fechou no meio de um jogo: devolve as notificações
        ProcessPause.ResumeAll(_settings);   // e retoma os programas pausados
        GameServices.Restart(_settings);     // e religa os serviços de disco
        _gm = new GameModeEngine(_settings) { CpuName = _cpuName };
        // O aviso de "jogo detectado" fica só na lista de eventos: aparecer na tela bem quando o jogo abre atrapalha.
        _gm.Notify += (title, text) => Dispatcher.BeginInvoke(() => Toast(title, text, "", quietInGame: !title.StartsWith("Modo Jogo encerrado", StringComparison.Ordinal)));
        try { _gm.SetLibrary(await Task.Run(() => GameLibrary.Scan(_settings.CustomGames))); } catch { }
        _gmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        // Fora da thread da janela: ao abrir o jogo o Modo Jogo chama powercfg e sc.exe, que travariam a janela, o overlay e as telas LCD.
        bool ticking = false;
        _gmTimer.Tick += async (_, _) =>
        {
            if (ticking) return;
            ticking = true;
            try { await Task.Run(() => _gm.Tick(_frames)); } catch { }
            finally { ticking = false; }
            if (PageGameMode.IsVisible) UpdateGameModeStatus();
        };
        _gmTimer.Start();
        CheckDriverChangedForShaders();
    }

    private void InitGameMode()
    {
        GmCleanRam.IsChecked = _settings.GameModeCleanRam;
        GmPower.IsChecked = _settings.GameModePowerPlan;
        GmAffinity.IsChecked = _settings.GameModeAffinity;
        GmCloseApps.IsChecked = _settings.GameModeCloseApps;
        GmTimer.IsChecked = _settings.GameModeTimer;
        GmQuiet.IsChecked = _settings.GameModeQuiet;
        GmMaxHz.IsChecked = _settings.GameModeMaxRefresh;
        GmSuspend.IsChecked = _settings.GameModeSuspend;
        GmSuspendList.Text = _settings.GameModeSuspendList;
        GmServices.IsChecked = _settings.GameModeServices;
        GmEcoQos.IsChecked = _settings.GameModeEcoQos;
        GmBgCores.IsChecked = _settings.GameModeBgCores;
        GmAvoidCpu0.IsChecked = _settings.GameModeAvoidCpu0;
        bool fewCores = Infrastructure.NativePerfApi.LastTwoCoresMask() == null;
        GmBgCores.IsEnabled = !fewCores || _settings.GameModeBgCores;
        if (fewCores) GmBgCoresText.Text = "Prender os programas de fundo nos 2 últimos threads (sem efeito nesta CPU: menos de 6 threads)";
        UpdateDisplayButtons();
        GmProBalance.IsChecked = _settings.GameModeProBalance;
        ProDlss.IsChecked = _settings.NvDlssLatest;
        UpdateCaptures();
        ProVrr.IsChecked = _settings.HasVrr;
        var mask = CpuTopology.GameCoresMask(out string what);
        GmAffinityText.Text = mask != null ? $"Prender o jogo nos núcleos mais rápidos para jogos: {what}"
                                           : "Prender o jogo nos núcleos mais rápidos (sem efeito nesta CPU: todos os núcleos são iguais)";
        GmAffinity.IsEnabled = mask != null;
        UpdateGameModeStatus();
        UpdateSessions();
        _ = UpdateShaderSizeAsync();
        if (!_gmPageInit) { _gmPageInit = true; _ = InitDriverProfilesAsync(); }
    }

    private void UpdateGameModeStatus()
    {
        GmStatus.Text = _gm?.Status ?? (_settings.AutoGameMode ? "Iniciando…" : "Desligado");
        BtnGameMode.Content = _settings.AutoGameMode ? "DESLIGAR" : "LIGAR";
    }

    private void BtnGameMode_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoGameMode = !_settings.AutoGameMode;
        _settings.Save();
        StartGameModeEngine();
        UpdateGameModeStatus();
    }

    private void GmOption_Click(object sender, RoutedEventArgs e)
    {
        _settings.GameModeCleanRam = GmCleanRam.IsChecked == true;
        _settings.GameModePowerPlan = GmPower.IsChecked == true;
        _settings.GameModeAffinity = GmAffinity.IsChecked == true;
        _settings.GameModeCloseApps = GmCloseApps.IsChecked == true;
        _settings.GameModeTimer = GmTimer.IsChecked == true;
        _settings.GameModeQuiet = GmQuiet.IsChecked == true;
        _settings.GameModeMaxRefresh = GmMaxHz.IsChecked == true;
        _settings.GameModeSuspend = GmSuspend.IsChecked == true;
        _settings.GameModeSuspendList = GmSuspendList.Text.Trim();
        _settings.GameModeServices = GmServices.IsChecked == true;
        _settings.GameModeEcoQos = GmEcoQos.IsChecked == true;
        _settings.GameModeBgCores = GmBgCores.IsChecked == true;
        _settings.GameModeAvoidCpu0 = GmAvoidCpu0.IsChecked == true;
        _settings.GameModeProBalance = GmProBalance.IsChecked == true;
        _settings.Save();
    }

    // ---------------- perfis do driver ----------------

    private int PrimaryHz() { try { return DisplayTuner.List().FirstOrDefault()?.CurrentHz ?? 60; } catch { return 60; } }

    private async Task InitDriverProfilesAsync()
    {
        string vendor = _gpus.FirstOrDefault(g => g.Vendor is "NVIDIA" or "AMD")?.Vendor ?? "";
        if (vendor == "AMD")
        {
            ProTitle.Text = "AJUSTES PRO NO AMD SOFTWARE: ADRENALIN EDITION";
            ProIntro.Text = AmdGuide(_settings.HasVrr, PrimaryHz());
            ProList.Visibility = BtnProApply.Visibility = BtnProRestore.Visibility = ProDlss.Visibility = Visibility.Collapsed;
            AmdSnapPanel.Visibility = Visibility.Visible;
            return;
        }
        if (vendor != "NVIDIA")
        {
            ProIntro.Text = _gpus.Count == 0 ? "Placa de vídeo ainda sendo detectada. Abra esta página de novo em alguns segundos."
                                             : "Perfis automáticos disponíveis para placas NVIDIA. Para AMD, esta página mostra o passo a passo no Adrenalin.";
            ProList.Visibility = BtnProApply.Visibility = BtnProRestore.Visibility = Visibility.Collapsed;
            _gmPageInit = _gpus.Count > 0;
            return;
        }

        ProIntro.Text = "Grava no driver NVIDIA (o mesmo do Painel de Controle NVIDIA) a configuração que os profissionais usam, " +
                        "separada por jogo: o driver aplica sozinho quando o jogo abre. Os jogos encontrados no PC já vêm marcados.";
        HashSet<GamePreset> installed;
        try
        {
            var lib = await Task.Run(() => GameLibrary.Scan(_settings.CustomGames));
            installed = await Task.Run(() => NvDriverProfiles.DetectInstalled(lib));
        }
        catch { installed = new(); }
        _proRows = NvDriverProfiles.Catalog
            .Select(p => new ProRow { Preset = p, Installed = installed.Contains(p), Selected = installed.Contains(p) })
            .OrderByDescending(r => r.Installed).ThenByDescending(r => r.Preset.Competitive).ToList();
        ProList.ItemsSource = _proRows;
        await RefreshDriverStatesAsync();
    }

    private async Task RefreshDriverStatesAsync()
    {
        NvDriverProfiles.ForceLatestDlss = _settings.NvDlssLatest && _gpus.Any(g => g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase));
        ProDlss.IsEnabled = _gpus.Any(g => g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase));
        bool vrr = _settings.HasVrr; int hz = PrimaryHz();
        ProVrrText.Text = $"Meu monitor tem G-SYNC / FreeSync e está ligado (monitor a {hz} Hz{(vrr ? $": trava em {NvDriverProfiles.VrrCap(hz)} FPS" : "")})";
        foreach (var r in _proRows) r.Recipe = NvDriverProfiles.Describe(r.Preset, vrr, hz);
        var states = await Task.Run(() => _proRows.Select(r => NvDriverProfiles.IsApplied(r.Preset, vrr, hz)).ToList());
        for (int i = 0; i < _proRows.Count; i++)
            _proRows[i].State = states[i] == true ? "PERFIL PRO ATIVO" : _proRows[i].Installed ? "INSTALADO" : "";
        if (NvDriverProfiles.Init() is string err) ProResult.Text = err;
    }

    private async void ProVrr_Click(object sender, RoutedEventArgs e)
    {
        _settings.HasVrr = ProVrr.IsChecked == true;
        _settings.NvDlssLatest = ProDlss.IsChecked == true;
        NvDriverProfiles.ForceLatestDlss = _settings.NvDlssLatest && _gpus.Any(g => g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        if (_proRows.Count > 0) await RefreshDriverStatesAsync();
        else if (ProList.Visibility != Visibility.Visible) ProIntro.Text = AmdGuide(_settings.HasVrr, PrimaryHz());
    }

    private async void BtnProApply_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        var sel = _proRows.Where(r => r.Selected).Select(r => r.Preset).ToList();
        if (sel.Count == 0) { ProResult.Text = "Marque pelo menos um jogo."; return; }
        bool vrr = _settings.HasVrr; int hz = PrimaryHz();
        BtnProApply.IsEnabled = false;
        ProResult.Text = "Gravando no driver…";
        var (ok, errors) = await Task.Run(() => NvDriverProfiles.Apply(sel, vrr, hz, _settings));
        BtnProApply.IsEnabled = true;
        await RefreshDriverStatesAsync();
        ProResult.Text = $"Perfis pro gravados em {ok} jogo(s). Vale a partir da próxima vez que o jogo abrir." +
                         (sel.Any(p => p.Reflex) ? " Nos jogos com NVIDIA Reflex, deixe o Reflex em \"Ligado + Boost\" nas opções de vídeo do jogo." : "") +
                         (errors.Count > 0 ? " Falhas: " + string.Join("; ", errors) : "");
    }

    private async void BtnProRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.TweakBackup.Keys.Any(k => k.StartsWith("nv."))) { ProResult.Text = "O tHg ainda não alterou o driver."; return; }
        if (!ThgDialog.Confirm("Restaurar driver", "Os perfis dos jogos no driver NVIDIA voltam para como estavam antes do tHg.", "Restaurar driver", "Cancelar")) return;
        string err = await Task.Run(() => NvDriverProfiles.RestoreAll(_settings));
        await RefreshDriverStatesAsync();
        ProResult.Text = err.Length == 0 ? "Driver restaurado." : $"Restaurado com avisos: {err}";
    }

    /// <summary>Gera, a partir do instantâneo exportado pelo AMD Software, uma cópia com os ajustes para jogos.</summary>
    private void BtnAmdSnap_Click(object sender, RoutedEventArgs e)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha o arquivo exportado pelo AMD Software",
            Filter = "Instantâneo do AMD Software (*.zip)|*.zip|Todos os arquivos|*.*",
            InitialDirectory = desktop,
        };
        if (dlg.ShowDialog(this) != true) return;
        string output = System.IO.Path.Combine(desktop, "tHg - AMD jogos.zip");
        if (string.Equals(dlg.FileName, output, StringComparison.OrdinalIgnoreCase))
            output = System.IO.Path.Combine(desktop, $"tHg - AMD jogos {DateTime.Now:HHmmss}.zip");
        try
        {
            var changes = AmdSnapshot.Generate(dlg.FileName, output);
            var custom = AmdSnapshot.CustomizedGames(dlg.FileName);
            string text = changes.Count == 0
                ? "Suas configurações globais já estavam como recomendado; a cópia foi criada igual."
                : "Criado \"tHg - AMD jogos.zip\" na Área de Trabalho com:\n• " + string.Join("\n• ", changes) +
                  "\n\nAgora, no AMD Software: Configurações → Sistema → Importar, escolha esse arquivo e confirme em até 15 segundos.";
            if (custom.Count > 0)
                text += $"\n\nAtenção: {string.Join(", ", custom)} tem perfil próprio no AMD Software, que ignora o global. Em Jogos → " +
                        $"{custom[0]}, use \"Redefinir\" (ou desligue Chill, Boost, Fluid Motion Frames e Enhanced Sync nele).";
            AmdSnapResult.Text = text;
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{output}\""); } catch { }
        }
        catch (Exception ex)
        {
            AmdSnapResult.Text = "Não foi possível gerar: " + Err(ex);
        }
    }

    private static string AmdGuide(bool vrr, int hz) =>
        "A AMD não oferece uma forma pública e segura de gravar essas opções por outro programa, então aqui vai a receita para aplicar " +
        "no AMD Software (botão direito na Área de Trabalho → AMD Software → Jogos → Gráficos, em Global ou no perfil de cada jogo):\n\n" +
        "COMPETITIVO (CS2, Valorant, LoL, Fortnite, Apex…)\n" +
        "  • Radeon Anti-Lag: Ligado (Anti-Lag 2 nos jogos que suportam, como o CS2)\n" +
        "  • Radeon Boost: Desligado · Radeon Chill: Desligado · Enhanced Sync: Desligado\n" +
        "  • Qualidade da filtragem de textura: Desempenho · Otimização do formato de superfície: Ligado\n" +
        (vrr
            ? $"  • AMD FreeSync: Ligado · Esperar pela atualização vertical: Sempre ligado · Radeon Chill/FRTC travado em {NvDriverProfiles.VrrCap(hz)} FPS\n"
            : "  • Esperar pela atualização vertical (V-Sync): Sempre desligado · sem limite de FPS\n") +
        "  • Radeon Image Sharpening: opcional, 50–80% ajuda a enxergar inimigos ao longe\n\n" +
        "ALTA FIDELIDADE (Cyberpunk, RDR2, GTA V…)\n" +
        "  • Anti-Lag: Ligado · Qualidade da filtragem de textura: Padrão\n" +
        (vrr ? $"  • FreeSync ligado com V-Sync e FPS travado em {NvDriverProfiles.VrrCap(hz)}\n" : "  • V-Sync: como o jogo pedir\n") +
        "  • FSR / HYPR-RX: use pelo menu do jogo quando ele tiver FSR 3/4 (fica melhor que forçar pelo driver)";

    // ---------------- cache de shaders ----------------

    private async Task UpdateShaderSizeAsync()
    {
        long size = await Task.Run(ShaderCache.Size);
        ShaderSize.Text = $"{size / 1048576.0:N0} MB";
    }

    private async void BtnShaderClean_Click(object sender, RoutedEventArgs e)
    {
        if (_gm?.Active == true) { ShaderResult.Text = "Feche o jogo antes de limpar o cache."; return; }
        BtnShaderClean.IsEnabled = false;
        long freed = await Task.Run(ShaderCache.Clean);
        BtnShaderClean.IsEnabled = true;
        ShaderResult.Text = $"{freed / 1048576.0:N0} MB liberados. As primeiras partidas de cada jogo vão recompilar os shaders.";
        await UpdateShaderSizeAsync();
    }

    /// <summary>Driver de vídeo mudou desde a última abertura: avisa para limpar o cache de shaders.</summary>
    private void CheckDriverChangedForShaders()
    {
        string? ver = _gpus.SelectMany(g => g.Sections).Where(s => s.Header == "DRIVER E BIOS")
                           .SelectMany(s => s.Children).FirstOrDefault(n => n.Header == "Versão do driver")?.Value;
        if (ver == null) return;
        if (_settings.LastGpuDriver != null && _settings.LastGpuDriver != ver)
        {
            ShaderHint.Text = "O driver de vídeo foi atualizado desde a última vez: limpar o cache agora evita engasgos nas primeiras partidas.";
            Toast("Driver de vídeo atualizado", "Limpe o cache de shaders em GAME › Modo Jogo para evitar engasgos.", "");
        }
        _settings.LastGpuDriver = ver;
        _settings.Save();
    }

    // ---------------- antes e depois ----------------

    private void UpdateSessions()
    {
        var all = _settings.GameSessions;
        GmSessions.ItemsSource = all.Take(8)
            .Select(s => new SessionRow($"{s.Game} · {s.Start:dd/MM HH:mm} · {s.Minutes:0} min",
                                       $"{s.AvgFps:0} FPS · 1% {s.Low1Fps:0}" + (s.Low01Fps > 0 ? $" · 0,1% {s.Low01Fps:0}" : "")))
            .ToList();
        if (_settings.TurboAppliedAt is not DateTime t)
        {
            GmCompareIntro.Text = "Jogue com o Modo Jogo ligado, depois aplique o TURBO: as sessões de antes e de depois aparecem comparadas aqui, jogo a jogo.";
            GmCompare.ItemsSource = null;
            return;
        }
        var rows = all.GroupBy(s => s.Game)
            .Select(g => (Game: g.Key, Before: g.Where(s => !s.AfterTurbo).ToList(), After: g.Where(s => s.AfterTurbo).ToList()))
            .Where(x => x.Before.Count > 0 && x.After.Count > 0)
            .Select(x =>
            {
                double b = x.Before.Average(s => s.AvgFps), a = x.After.Average(s => s.AvgFps);
                double bl = x.Before.Average(s => s.Low1Fps), al = x.After.Average(s => s.Low1Fps);
                string delta = b > 0 ? $"{(a / b - 1) * 100:+0;-0}%" : "";
                return new MeterItem(x.Game, $"{b:0} → {a:0} FPS ({delta}) · 1% low {bl:0} → {al:0}", Math.Min(1, b > 0 ? a / (Math.Max(a, b) * 1.0) : 0));
            }).ToList();
        GmCompareIntro.Text = rows.Count == 0
            ? $"TURBO aplicado em {t:dd/MM/yyyy}. Assim que um mesmo jogo tiver sessões antes e depois dessa data, a comparação aparece aqui."
            : $"Média das sessões de cada jogo antes e depois do TURBO ({t:dd/MM/yyyy}). Mesma resolução e mesmos gráficos deixam a comparação justa.";
        GmCompare.ItemsSource = rows;
    }

    // ---------------- tela: HDR e OLED ----------------

    private void UpdateDisplayButtons()
    {
        try
        {
            var hdr = DisplayExtras.HdrDisplays().Where(d => d.Supported).ToList();
            BtnHdr.IsEnabled = hdr.Count > 0;
            BtnHdr.Content = hdr.Count == 0 ? "HDR: SEM MONITOR COMPATÍVEL" : hdr.Any(d => d.Enabled) ? "DESLIGAR O HDR" : "LIGAR O HDR";
            BtnOledBar.Content = DisplayExtras.TaskbarAutoHide ? "MOSTRAR A BARRA DE TAREFAS" : "ESCONDER A BARRA DE TAREFAS";
        }
        catch { }
    }

    private void BtnHdr_Click(object sender, RoutedEventArgs e)
    {
        bool on = !DisplayExtras.HdrDisplays().Any(d => d.Supported && d.Enabled);
        int n = DisplayExtras.SetHdr(on);
        DisplayStatus.Text = n > 0 ? $"HDR {(on ? "ligado" : "desligado")} em {n} monitor(es)." : "O Windows não aceitou a mudança do HDR.";
        UpdateDisplayButtons();
    }

    private void BtnOledBar_Click(object sender, RoutedEventArgs e)
    {
        DisplayExtras.SetTaskbarAutoHide(!DisplayExtras.TaskbarAutoHide, _settings);
        _settings.Save();
        UpdateDisplayButtons();
    }

    private void BtnOledOff_Click(object sender, RoutedEventArgs e)
    {
        DisplayExtras.SetScreenOff(5, _settings);
        _settings.Save();
        DisplayStatus.Text = "A tela desliga depois de 5 minutos sem uso (na tomada). \"Desfazer\" volta ao tempo anterior.";
    }

    private void BtnOledRestore_Click(object sender, RoutedEventArgs e)
    {
        DisplayExtras.RestoreOled(_settings);
        DisplayStatus.Text = "Barra de tarefas e desligamento da tela voltaram como estavam.";
        UpdateDisplayButtons();
    }
}
