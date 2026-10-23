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
        SetStartWin.IsEnabled = false;
        // schtasks demora alguns décimos: lê fora da janela.
        SetStartWin.IsChecked = await RefreshStartsWithWindowsAsync();
        SetStartWin.IsEnabled = true;
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
        SetStartWin.IsEnabled = false;
        SetStartWin.IsChecked = await ToggleStartWithWindowsAsync();
        SetStartWin.IsEnabled = true;
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
        SetCheckUpdate.IsEnabled = false;
        try { await CheckUpdateAsync(manual: true); }
        finally { SetCheckUpdate.IsEnabled = true; }
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
}
