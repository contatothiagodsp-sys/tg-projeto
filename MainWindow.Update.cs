using System.Windows;
using System.Windows.Input;

namespace ThgOptimization;

/// <summary>
/// Atualização dentro do app: confere o GitHub ao abrir (no máximo a cada 12 horas), avisa quando há versão nova e
/// atualiza com um clique (baixa, confere, instala em silêncio e reabre). As configurações e ajustes ficam.
/// </summary>
public partial class MainWindow
{
    private UpdateInfo? _update;
    private bool _updating;

    private async void StartUpdateCheck()
    {
        SideVersion.Cursor = Cursors.Hand;
        SideVersion.ToolTip = "Clique para procurar atualização";
        SideVersion.MouseLeftButtonUp += (_, _) => _ = CheckUpdateAsync(manual: true);
        if (AppUpdater.Repo == null || !_settings.AutoCheckUpdates) return;
        if (_settings.LastUpdateCheck is DateTime last && (DateTime.Now - last).TotalHours < 12) return;
        await Task.Delay(20_000); // depois da abertura, sem disputar com os diagnósticos
        await CheckUpdateAsync(manual: false);
    }

    private async Task CheckUpdateAsync(bool manual)
    {
        if (_updating) return;
        if (_update != null) { await InstallUpdateAsync(_update); return; }
        if (AppUpdater.Repo == null)
        {
            if (manual) ThgDialog.Info("Atualizações", $"Você está na versão {AppUpdater.Current}. Esta cópia foi gerada no seu PC, sem ligação " +
                                       "com uma página de versões no GitHub: para atualizar, gere o instalador de novo com o código mais novo.");
            return;
        }
        string before = SideVersion.Text;
        if (manual) SideVersion.Text = "Procurando atualização…";
        try
        {
            _update = await AppUpdater.CheckAsync();
            _settings.LastUpdateCheck = DateTime.Now; _settings.Save();
        }
        catch (Exception ex)
        {
            SideVersion.Text = before;
            if (manual) ThgDialog.Info("Atualizações", $"Não foi possível consultar o GitHub agora: {ex.Message}", MessageBoxImage.Warning);
            return;
        }
        SideVersion.Text = before;
        if (_update == null)
        {
            if (manual) ThgDialog.Info("Atualizações", $"Você já está na versão mais nova ({AppUpdater.Current}).");
            return;
        }
        SideVersion.Text = $"Atualização {_update.Version} disponível · clique";
        SideVersion.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Accent");
        if (manual) await InstallUpdateAsync(_update);
        else Toast("Nova versão do tHg", $"A versão {_update.Version} está disponível. Clique na versão, no canto do menu, ou use o menu da bandeja para atualizar.", "");
    }

    private async Task InstallUpdateAsync(UpdateInfo u)
    {
        if (_updating) return;
        if (_gm?.Active == true && !ThgDialog.Confirm("Atualizar agora?", "Um jogo está aberto. O tHg vai fechar por alguns segundos durante a atualização.", "Atualizar", "Depois"))
            return;
        string notes = u.Notes.Trim();
        if (notes.Length > 700) notes = notes[..700].TrimEnd() + "…";
        if (!ThgDialog.Confirm($"Atualizar para a versão {u.Version}",
                               $"Você está na {AppUpdater.Current}. O tHg baixa a versão nova ({u.Size / 1048576.0:0} MB), instala e abre de novo sozinho. " +
                               "Suas configurações e ajustes continuam." + (notes.Length > 0 ? "\n\nNovidades:\n" + notes : ""), "Atualizar", "Agora não",
                               MessageBoxImage.Information)) return;
        _updating = true;
        string label = SideVersion.Text;
        try
        {
            var progress = new Progress<double>(p => SideVersion.Text = $"Baixando atualização… {p:P0}");
            string file = await AppUpdater.DownloadAsync(u, progress, CancellationToken.None);
            SideVersion.Text = "Instalando…";
            _settings.ResumeInTray = !IsVisible;
            _settings.Save();
            AppUpdater.RunInstaller(file, u);
            await Task.Delay(800);
            ExitApp();
        }
        catch (Exception ex)
        {
            _updating = false;
            SideVersion.Text = label;
            ThgDialog.Info("Atualizações", $"A atualização não foi concluída: {ex.Message}", MessageBoxImage.Warning);
        }
    }

    /// <summary>Aberto pelo instalador depois de atualizar.</summary>
    public void AnnounceUpdated()
    {
        bool tray = _settings.ResumeInTray;
        _settings.ResumeInTray = false; _settings.Save();
        if (tray) StartInTray(); else Show();
        Toast("tHg Optimization atualizado", $"Agora na versão {AppUpdater.Current}. Veja as novidades na página do GitHub.", "");
    }
}
