using System.Windows;
using System.Windows.Input;

namespace ThgOptimization;

/// <summary>
/// Atualização dentro do app: confere o GitHub sempre que o app abre (inclusive com o Windows, ao ligar o PC) e, enquanto
/// ele roda (fica dias na bandeja), a cada 6 horas. Com "Atualizar automaticamente" ligado, a versão nova é baixada,
/// conferida, instalada em silêncio e o app reabre sozinho; com um jogo aberto, espera o jogo fechar. Desligado, só avisa
/// e atualiza com um clique. As configurações e ajustes ficam.
/// </summary>
public partial class MainWindow
{
    private UpdateInfo? _update;
    private bool _updating;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);

    private async void StartUpdateCheck()
    {
        SideVersion.Cursor = Cursors.Hand;
        SideVersion.ToolTip = "Clique para procurar atualização";
        SideVersion.MouseLeftButtonUp += (_, _) => _ = CheckUpdateAsync(manual: true);
        if (AppUpdater.Repo == null) return;
        // Voltou de uma atualização: a versão em uso já é a que foi instalada, então a tentativa está resolvida.
        if (Version.TryParse(_settings.UpdateTriedVersion, out var tried) && AppUpdater.Current >= tried)
        {
            _settings.UpdateTriedVersion = null; _settings.Save();
        }
        await Task.Delay(20_000); // depois da abertura, sem disputar com os diagnósticos
        bool first = true; // ao abrir o app (ou o PC ligar) confere sempre, sem esperar as 6 horas
        while (!_updating)
        {
            bool due = first || !(_settings.LastUpdateCheck is DateTime last && DateTime.Now - last < UpdateInterval);
            first = false;
            if (_settings.AutoCheckUpdates && due)
            {
                if (_update == null) await CheckUpdateAsync(manual: false);
                else await AutoInstallAsync(_update); // já sabia da versão nova (ex.: um jogo estava aberto)
            }
            await Task.Delay(TimeSpan.FromMinutes(_update != null ? 10 : 60));
        }
    }

    /// <summary>Instala sem perguntar, exceto com jogo aberto ou se esta mesma versão já foi tentada e não pegou.</summary>
    private async Task AutoInstallAsync(UpdateInfo u)
    {
        if (_updating || _gm?.Active == true) return; // com jogo aberto, tenta de novo daqui a 10 minutos
        if (_settings.UpdateTriedVersion == u.Version.ToString())
        {
            // O instalador desta versão já rodou e o app continua na anterior: não entra em ciclo de reinstalar.
            SideVersion.ToolTip = $"A instalação automática da {u.Version} não foi concluída. Clique para tentar de novo.";
            return;
        }
        await InstallUpdateAsync(u, ask: false);
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
            if (manual) ThgDialog.Info("Atualizações", $"Não foi possível consultar o GitHub agora: {Err(ex)}", MessageBoxImage.Warning);
            return;
        }
        SideVersion.Text = before;
        if (_update == null)
        {
            SideVersion.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Faint");
            SideVersion.ToolTip = $"Versão {AppUpdater.Current} · atualizado. Clique para procurar de novo.";
            // Janela, não aviso no canto: o aviso some sozinho em tela cheia/Não Perturbe e parecia que nada aconteceu.
            if (manual) ThgDialog.Info("Atualizações", $"Você já está na versão mais nova ({AppUpdater.Current}).");
            return;
        }
        SideVersion.Text = $"Atualização {_update.Version} disponível · clique";
        SideVersion.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Accent");
        if (manual) await InstallUpdateAsync(_update);
        else if (_settings.AutoCheckUpdates) await AutoInstallAsync(_update);
        else Toast("Nova versão do tHg", $"A versão {_update.Version} está disponível. Clique na versão, no canto do menu, ou use o menu da bandeja para atualizar.", "");
    }

    private async Task InstallUpdateAsync(UpdateInfo u, bool ask = true)
    {
        if (_updating) return;
        if (ask && _gm?.Active == true && !ThgDialog.Confirm("Atualizar agora?", "Um jogo está aberto. O tHg vai fechar por alguns segundos durante a atualização.", "Atualizar", "Depois"))
            return;
        string notes = u.Notes.Trim();
        if (notes.Length > 700) notes = notes[..700].TrimEnd() + "…";
        if (ask && !ThgDialog.Confirm($"Atualizar para a versão {u.Version}",
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
            _settings.UpdateTriedVersion = u.Version.ToString();
            _settings.Save();
            AppUpdater.RunInstaller(file, u);
            await Task.Delay(800);
            ExitApp();
        }
        catch (Exception ex)
        {
            _updating = false;
            SideVersion.Text = label;
            if (ask) ThgDialog.Info("Atualizações", $"A atualização não foi concluída: {Err(ex)}", MessageBoxImage.Warning);
            else SideVersion.ToolTip = $"A atualização automática falhou ({Err(ex)}). Clique para tentar de novo.";
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
