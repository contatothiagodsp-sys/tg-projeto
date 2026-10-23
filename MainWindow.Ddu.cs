using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ThgOptimization;

/// <summary>
/// FERRAMENTAS · DDU: detecta a placa, baixa o driver mais recente, remove o driver atual por completo (no Modo de
/// Segurança ou não) e termina como o DDU: reiniciar, sem reiniciar ou desligar; o driver novo instala depois sozinho.
/// </summary>
public partial class MainWindow
{
    private VideoVendor _dduVendor = VideoVendor.Nvidia;
    private bool _dduInit, _dduBusy;
    private GpuDriverStatus? _dduGpu;

    private void InitDdu()
    {
        if (!_dduInit)
        {
            _dduInit = true;
            _ = DetectDduGpuAsync();
        }
        UpdateDduButtons();
        BtnDduUnblock.IsEnabled = DduCleaner.WindowsUpdateBlocked(_settings);
        // Última limpeza (sem a data e sem a lista de arquivos, que fica só no arquivo do registro).
        DduLog.Text = string.Join("\n", DduCleaner.ReadLogFrom(Math.Max(0, DduCleaner.LogLength() - 24_000)).Lines.TakeLast(80));
        if (DduLog.Text.Length == 0) DduLog.Text = "Nenhuma limpeza feita ainda.";

        // Reaberto no meio de uma limpeza: volta a acompanhar ao vivo.
        if (!_dduBusy && DduCleaner.RunningWorker() is { } running) _ = FollowWorkerAsync(running);
    }

    private async Task FollowWorkerAsync(System.Diagnostics.Process worker)
    {
        _dduBusy = true;
        BtnDduRestart.IsEnabled = BtnDduNoRestart.IsEnabled = BtnDduShutdown.IsEnabled = false;
        DduLog.Text += "\n\n▶ Limpeza em andamento (continuou enquanto o app estava fechado)…\n";
        long pos = DduCleaner.LogLength();
        while (!worker.HasExited)
        {
            await Task.Delay(500);
            var (lines, np) = DduCleaner.ReadLogFrom(pos); pos = np;
            foreach (var l in lines) DduLog.Text += l + "\n";
        }
        foreach (var l in DduCleaner.ReadLogFrom(pos).Lines) DduLog.Text += l + "\n";
        _dduBusy = false;
        BtnDduRestart.IsEnabled = BtnDduShutdown.IsEnabled = true;
        UpdateDduButtons();
    }

    // ---------------- placa detectada e driver mais recente ----------------

    private async Task DetectDduGpuAsync()
    {
        List<GpuDriverStatus> gpus;
        try { gpus = await Task.Run(DriverManager.ReadGpus); } catch { gpus = new(); }
        // Placa dedicada primeiro: num PC com vídeo integrado + NVIDIA/AMD, é a dedicada que interessa.
        // Placa dedicada primeiro (RX, RTX, GTX, Arc). O vídeo integrado do processador ("Radeon(TM) Graphics", "UHD") fica por último.
        static int Rank(GpuDriverStatus g) =>
            System.Text.RegularExpressions.Regex.IsMatch(g.GpuName, @"\b(RX|RTX|GTX|Arc|Radeon Pro|Quadro)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 0
            : g.Vendor is "NVIDIA" or "AMD" ? 1 : 2;
        _dduGpu = gpus.OrderBy(Rank).FirstOrDefault();
        if (gpus.Count > 1)
            DduDlStatus.Text = "Também neste PC: " + string.Join(", ", gpus.Where(g => g != _dduGpu).Select(g => g.GpuName)) +
                               ". A limpeza da marca remove o driver de todas as placas dela.";
        if (_dduGpu == null)
        {
            DduGpuName.Text = "Nenhuma placa de vídeo encontrada";
            DduNvidia.IsChecked = true;
            return;
        }
        DduGpuName.Text = _dduGpu.GpuName;
        DduDrvInstalled.Text = _dduGpu.NoDriver ? "instalado: nenhum (sem driver)" : $"instalado: {(_dduGpu.Installed.Length > 0 ? _dduGpu.Installed : "nenhum")}";
        DduDrvInstalled.SetResourceReference(TextBlock.ForegroundProperty, _dduGpu.NoDriver ? "Warn" : "Fg");
        (_dduGpu.Vendor == "AMD" ? DduAmd : _dduGpu.Vendor == "Intel" ? DduIntel : DduNvidia).IsChecked = true;

        try
        {
            if (_dduGpu.Vendor == "NVIDIA") await DriverManager.CheckNvidiaAsync(_dduGpu, false, CancellationToken.None);
            else if (_dduGpu.Vendor == "AMD") await DriverManager.CheckAmdAsync(_dduGpu, CancellationToken.None);
        }
        catch { }
        bool newer = _dduGpu.UpdateAvailable;
        DduDrvLatest.Text = _dduGpu.Latest.Length > 0 ? $"mais recente: {_dduGpu.Latest}{(newer ? " · novo" : " · em dia")}" : "mais recente: veja o site oficial";
        DduDrvLatestPill.SetResourceReference(Border.BackgroundProperty, newer ? "WarnSoft" : _dduGpu.Latest.Length > 0 ? "GoodSoft" : "Hover");
        DduDrvLatest.SetResourceReference(TextBlock.ForegroundProperty, newer ? "Warn" : _dduGpu.Latest.Length > 0 ? "Good" : "Muted");
        BtnDduDownload.Content = _dduGpu.CanAutoInstall ? $"BAIXAR DRIVER {_dduGpu.Latest}".Trim() : "ABRIR SITE DO DRIVER";
        ShowPendingDriver();
    }

    /// <summary>Driver já baixado (antes da limpeza, por exemplo): mostra e troca o botão para instalar.</summary>
    private void ShowPendingDriver()
    {
        if (DriverInstallers.Ready(_settings) is not { } p)
        {
            DduInstallAfter.Visibility = Visibility.Collapsed;
            return;
        }
        long mb = new FileInfo(p.Path).Length / 1048576;
        DduDlStatus.Text = $"Baixado em {p.Downloaded:dd/MM HH:mm}: {p.Vendor} {p.Version} ({mb} MB) para {p.Gpu}. " +
                           (_dduGpu?.NoDriver == true ? "A placa está sem driver: instale agora." : "Pronto para a instalação limpa depois da DDU.");
        DduInstallAfter.Visibility = Visibility.Visible;
        BtnDduDownload.IsEnabled = true;
        BtnDduDownload.Content = "INSTALAR DRIVER BAIXADO";
        BtnDduDownload.Style = (Style)FindResource(_dduGpu?.NoDriver == true ? "PrimaryButton" : "OutlineButton");
    }

    private async void BtnDduDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_dduGpu == null) return;
        if (DriverInstallers.Ready(_settings) != null)
        {
            string err = DriverInstallers.Launch(_settings);
            DduDlStatus.Text = err.Length == 0
                ? "Instalador aberto. Siga as telas do fabricante; quando terminar, o tHg apaga o instalador e os arquivos extraídos sozinho."
                : $"Não foi possível abrir: {err}";
            return;
        }
        if (!_dduGpu.CanAutoInstall)
        {
            string url = _dduGpu.OfficialPage.Length > 0 ? _dduGpu.OfficialPage
                       : _dduGpu.Vendor == "Intel" ? "https://www.intel.com/content/www/us/en/download-center/home.html" : "https://www.nvidia.com/Download/index.aspx";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            return;
        }
        BtnDduDownload.IsEnabled = false;
        DduDlBar.Visibility = Visibility.Visible;
        try
        {
            string file = await DriverManager.DownloadAsync(_dduGpu,
                (text, frac) => Dispatcher.Invoke(() => { DduDlStatus.Text = text; DduDlProgress.ScaleX = frac; }), CancellationToken.None);
            DriverInstallers.Register(_settings, file, _dduGpu);
            ShowPendingDriver();
        }
        catch (Exception ex)
        {
            DduDlStatus.Text = $"Não foi possível baixar: {ex.Message}";
            BtnDduDownload.IsEnabled = true;
        }
        DduDlBar.Visibility = Visibility.Collapsed;
    }

    // ---------------- análise ----------------

    private async void DduVendor_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || DduFound == null) return; // disparado no InitializeComponent
        _dduVendor = Enum.Parse<VideoVendor>(tag);
        // Monitores e cache de shaders são coisas de vídeo.
        bool audio = DduCleaner.IsAudio(_dduVendor);
        DduMonitors.IsEnabled = DduShaders.IsEnabled = !audio;
        await ScanDduAsync();
    }

    private async void BtnDduScan_Click(object sender, RoutedEventArgs e) => await ScanDduAsync();

    private async Task ScanDduAsync()
    {
        if (_dduBusy) return;
        _dduBusy = true;
        var v = _dduVendor;
        DduFound.Text = $"Procurando {DduCleaner.What(v)}…";
        try
        {
            var (devs, pkgs) = await Task.Run(() => (DduCleaner.Devices(v), DduCleaner.Packages(v)));
            DduFound.Text = devs.Count == 0 && pkgs.Count == 0
                ? $"Nada encontrado para remover ({DduCleaner.Name(v)})."
                : $"Dispositivos: {(devs.Count == 0 ? "nenhum conectado" : string.Join(", ", devs.Select(d => d.Name)))}\n" +
                  $"Pacotes no Windows: {pkgs.Count}" + (pkgs.Count > 0 ? "\n  " + string.Join("\n  ", pkgs.Take(12).Select(p => p.Display)) +
                                                          (pkgs.Count > 12 ? $"\n  … e mais {pkgs.Count - 12}" : "") : "");
        }
        catch (Exception ex) { DduFound.Text = $"Não foi possível analisar: {ex.Message}"; }
        _dduBusy = false;
    }

    // ---------------- limpeza ----------------

    private void DduSafeMode_Click(object sender, RoutedEventArgs e) => UpdateDduButtons();

    private void UpdateDduButtons()
    {
        bool safe = DduSafeMode.IsChecked == true;
        BtnDduNoRestart.IsEnabled = !safe && !_dduBusy;
        DduRestorePoint.IsEnabled = true;
    }

    private DduOptions DduOpts(DduFinish finish) => new(
        DduBackup.IsChecked == true, DduBlockWu.IsChecked == true, DduShaders.IsChecked == true, DduLeftovers.IsChecked == true,
        DduMonitors.IsChecked == true, DduStoreApps.IsChecked == true, DduRestorePoint.IsChecked == true,
        DriverInstallers.Ready(_settings) is { } pd && DduInstallAfter.IsChecked == true ? pd.Path : null, finish);

    private async void BtnDduClean_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin() || sender is not Button { Tag: string tag }) return;
        var finish = Enum.Parse<DduFinish>(tag);
        bool safe = DduSafeMode.IsChecked == true;
        string what = DduCleaner.What(_dduVendor);
        var o = DduOpts(finish);
        string end = finish switch
        {
            DduFinish.Shutdown => "No fim o PC desliga: troque a placa de vídeo e ligue de novo.",
            DduFinish.None => "No fim o PC continua ligado.",
            _ => "No fim o PC reinicia no Windows normal.",
        };
        string install = o.InstallerPath != null
            ? (finish == DduFinish.None ? " Em seguida o instalador do driver novo abre." : " Depois que você entrar no Windows, o instalador do driver novo abre sozinho.")
            : " Até instalar um driver novo, a tela fica em resolução básica.";
        string text = safe
            ? $"O PC vai reiniciar no Modo de Segurança e o tHg remove {what} sozinho (uns 3 a 5 minutos). {end}{install}\n\nSalve e feche o que estiver aberto antes."
            : $"Vai ser removido {what} com o Windows aberto. A tela pode piscar. {end}{install}\n\nO Modo de Segurança limpa melhor; use este modo se não puder reiniciar agora.";
        string button = finish switch { DduFinish.Shutdown => "Limpar e desligar", DduFinish.None => "Limpar agora", _ => "Limpar e reiniciar" };
        if (!ThgDialog.Confirm("DDU", text, button, "Cancelar", MessageBoxImage.Warning)) return;

        if (safe)
        {
            string err = DduCleaner.ScheduleSafeMode(_dduVendor, o, _settings);
            if (err.Length > 0) ThgDialog.Info("DDU", err, MessageBoxImage.Warning);
            return;
        }

        _dduBusy = true;
        BtnDduRestart.IsEnabled = BtnDduNoRestart.IsEnabled = BtnDduShutdown.IsEnabled = false;
        DduLog.Text = "";
        // A tela vai piscar quando a placa sair: desenha sem a placa de vídeo e deixa a limpeza num processo separado,
        // que termina (e reinicia, se for o caso) mesmo que esta janela feche.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        long pos = DduCleaner.LogLength();
        var worker = DduCleaner.StartWorker(_dduVendor, o, finish: true);
        while (!worker.HasExited)
        {
            await Task.Delay(400);
            var (lines, np) = DduCleaner.ReadLogFrom(pos); pos = np;
            foreach (var l in lines) DduLog.Text += l + "\n";
        }
        foreach (var l in DduCleaner.ReadLogFrom(pos).Lines) DduLog.Text += l + "\n";
        int removed = worker.ExitCode;
        if (removed == 0)
            ThgDialog.Info("DDU", "Nada foi removido, então o PC não vai reiniciar. Veja o registro da limpeza para o motivo.", MessageBoxImage.Warning);
        _dduBusy = false;
        BtnDduRestart.IsEnabled = BtnDduShutdown.IsEnabled = true;
        UpdateDduButtons();
        BtnDduUnblock.IsEnabled = DduCleaner.WindowsUpdateBlocked(_settings);
    }

    private void BtnDduUnblock_Click(object sender, RoutedEventArgs e)
    {
        DduCleaner.UnblockWindowsUpdate(_settings);
        BtnDduUnblock.IsEnabled = false;
        DduLog.Text += "\nWindows Update liberado para instalar drivers de novo.";
    }

    private void BtnDduOpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(DduCleaner.LogPath)) try { Process.Start(new ProcessStartInfo(DduCleaner.LogPath) { UseShellExecute = true }); } catch { }
        else DduLog.Text = "Nenhuma limpeza feita ainda.";
    }
}
