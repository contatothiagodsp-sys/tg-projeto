using System.Windows;
using System.Windows.Controls;

namespace ThgOptimization;

/// <summary>Página Configurações: tema, iniciar com o Windows, bandeja e atualizações (antes só no menu da bandeja).</summary>
public partial class MainWindow
{
    private async void InitSettings()
    {
        SyncSettingsTheme();
        SetCloseTray.IsChecked = _settings.CloseToTray;
        SetAutoUpdate.IsChecked = _settings.AutoCheckUpdates;
        SetVersion.Text = $"Versão instalada: {AppUpdater.Current}";
        SetShowAllModules.IsChecked = _settings.ShowAllHardwareModules;
        UpdateMemStats();
        UpdateHwSummary();
        // schtasks demora alguns décimos: lê fora da janela.
        await Infrastructure.UiGuard.RunButtonAsync(SetStartWin, "config",
            async () => SetStartWin.IsChecked = await RefreshStartsWithWindowsAsync(),
            ex => Toast("Iniciar com o Windows", "Não foi possível ler: " + Err(ex), ""));
    }

    /// <summary>Só ao abrir a página ou clicar em ATUALIZAR: medir a memória não deve custar nada no resto do tempo.</summary>
    private void UpdateMemStats() => SetMemStats.Text = Infrastructure.MemorySnapshot.Read().Summary;

    private void SetMemRefresh_Click(object sender, RoutedEventArgs e) => UpdateMemStats();

    /// <summary>Custo real neste PC, com os sensores ligados como sempre estão. Roda só quando pedido.</summary>
    private async void SetMemMeasure_Click(object sender, RoutedEventArgs e)
    {
        SetMemStats.Text = "Medindo por 10 s…";
        await Infrastructure.UiGuard.RunButtonAsync(sender as UIElement, "memória", async () =>
        {
            var result = await Infrastructure.ResourceProbe.MeasureAsync(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));
            SetMemStats.Text = result.Summary;
            Infrastructure.AppLog.Info("memória", result.Summary);
        }, ex => SetMemStats.Text = "Não foi possível medir: " + Err(ex));
    }

    private void SyncSettingsTheme()
    {
        if (SetThemeSystem == null) return;
        SetThemeSystem.IsChecked = ThemeManager.Mode == ThemeManager.System;
        SetThemeDark.IsChecked = ThemeManager.Mode == ThemeManager.Dark;
        SetThemeLight.IsChecked = ThemeManager.Mode == ThemeManager.Light;
    }

    private void SetTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string mode }) return;
        ThemeManager.SetMode(mode);
        _settings.ThemeMode = ThemeManager.Mode;
        _settings.LightTheme = ThemeManager.IsLight;
        try { _settings.Save(); } catch { }
    }

    private async void SetStartWin_Click(object sender, RoutedEventArgs e)
    {
        await Infrastructure.UiGuard.RunButtonAsync(SetStartWin, "config",
            async () => SetStartWin.IsChecked = await ToggleStartWithWindowsAsync(),
            ex => Toast("Iniciar com o Windows", "Não foi possível alterar: " + Err(ex), ""));
    }

    private void SetCloseTray_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = SetCloseTray.IsChecked == true;
        _settings.Save();
    }

    private void SetAutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoCheckUpdates = SetAutoUpdate.IsChecked == true;
        _settings.Save();
    }

    private async void SetCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await Infrastructure.UiGuard.RunButtonAsync(SetCheckUpdate, "atualizacao", () => CheckUpdateAsync(manual: true),
            ex => Toast("Atualização", "Não foi possível verificar: " + Err(ex), ""));
    }

    private void SetOpenData_Click(object sender, RoutedEventArgs e) => OpenShell(AppSettings.Folder);

    private void SetReport_Click(object sender, RoutedEventArgs e)
    {
        if (AppUpdater.Repo is string repo) OpenShell($"https://github.com/{repo}/issues/new");
        else Toast("Relatar um problema", "Esta cópia foi gerada no seu PC, sem ligação com o repositório do GitHub.", "");
    }

    private static void OpenShell(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // ---------------- módulos e hardware ----------------

    /// <summary>
    /// Detecção de hardware: na abertura (em segundo plano; o resultado anterior já vale enquanto isso) e pelo botão
    /// REESCANEAR. O primeiro scan e o manual mostram o resumo do que foi encontrado.
    /// </summary>
    private async Task RunHardwareScanAsync(bool manual)
    {
        if (manual) { SetRescanHw.IsEnabled = false; SetHwSummary.Text = "Procurando hardware e periféricos…"; }
        try
        {
            var caps = await Infrastructure.HardwareCapabilities.DetectAsync();
            bool first = !_settings.FirstHardwareScanDone;
            _settings.HardwareCaps = caps;
            _settings.FirstHardwareScanDone = true;
            try { _settings.Save(); } catch { }
            ApplyHardwareVisibility();
            UpdateHwSummary();
            if (manual || first)
            {
                int hidden = _hiddenPages.Count + (IntegAulaCard.Visibility == Visibility.Collapsed ? 1 : 0) + (IntegCorsairBox.Visibility == Visibility.Collapsed ? 1 : 0);
                Toast(first ? "Hardware detectado" : "Hardware reescaneado",
                      caps.Summary() + (hidden > 0 ? $" · {hidden} módulo(s) sem hardware escondido(s) (Configurações → Mostrar todos os módulos)" : ""), "");
            }
        }
        catch (Exception ex)
        {
            if (manual) Toast("Reescanear hardware", "Não foi possível detectar: " + Err(ex), "");
        }
        finally { if (manual) SetRescanHw.IsEnabled = true; }
    }

    private void UpdateHwSummary()
    {
        if (SetHwSummary == null) return;
        SetHwSummary.Text = _settings.HardwareCaps is { } c
            ? $"{c.Summary()} · verificado em {c.ScannedAt:dd/MM HH:mm}"
            : "O hardware ainda não foi detectado: todos os módulos aparecem.";
    }

    private void SetShowAllModules_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowAllHardwareModules = SetShowAllModules.IsChecked == true;
        try { _settings.Save(); } catch { }
        ApplyHardwareVisibility();
    }

    private async void SetRescanHw_Click(object sender, RoutedEventArgs e) => await RunHardwareScanAsync(manual: true);
}
