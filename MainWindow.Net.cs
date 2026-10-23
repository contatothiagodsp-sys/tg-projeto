using System.Windows;
using System.Windows.Threading;

namespace ThgOptimization;

/// <summary>Páginas Energia (bateria e fonte) e Rede (diagnóstico de lag).</summary>
public partial class MainWindow
{
    // ================= Energia =================

    private bool _energyBusy;

    private async void RefreshEnergy()
    {
        if (_energyBusy) return;
        _energyBusy = true;
        BtnEnergyRefresh.IsEnabled = false;
        EnergyStatus.Text = "Lendo bateria e energia…";
        try
        {
            var sensors = _monitor.Rows.ToList();
            var cards = await Task.Run(() => BatteryInfo.Read(sensors));
            EnergyCards.ItemsSource = cards;
            bool hasBattery = cards.Count > 1;
            EnergyStatus.Text = (hasBattery ? "Bateria detectada" : "Nenhuma bateria (PC de mesa)") + $" · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { EnergyStatus.Text = "Falhou: " + Err(ex); }
        finally
        {
            _energyBusy = false;
            BtnEnergyRefresh.IsEnabled = true;
        }
    }

    private void BtnEnergyRefresh_Click(object sender, RoutedEventArgs e) => RefreshEnergy();

    // ================= Rede =================

    private CancellationTokenSource? _netCts;
    private bool _netRunning;
    private List<DiskHealthCard>? _lastNet;

    private async void BtnNetTest_Click(object sender, RoutedEventArgs e)
    {
        if (_netRunning) { _netCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { NetStatus.Text = "Termine o outro teste antes."; return; }

        _netRunning = true;
        _netCts = new CancellationTokenSource();
        var ct = _netCts.Token;
        BtnNetTest.Content = "CANCELAR";
        NetCards.ItemsSource = null;
        NetProgress.ScaleX = 0;
        try
        {
            var cards = await NetworkTest.Run((text, frac) => Dispatcher.BeginInvoke(new Action(() =>
            {
                NetStatus.Text = text;
                NetProgress.ScaleX = Math.Clamp(frac, 0, 1);
            })), ct);
            _lastNet = cards;
            NetCards.ItemsSource = cards;
            NetStatus.Text = $"Concluído às {DateTime.Now:HH:mm:ss}. O resultado entra no relatório exportado.";
            NetProgress.ScaleX = 1;
        }
        catch (OperationCanceledException) { NetStatus.Text = "Teste cancelado."; NetProgress.ScaleX = 0; }
        catch (Exception ex) { NetStatus.Text = "Falhou: " + Err(ex); NetProgress.ScaleX = 0; }
        finally
        {
            _netRunning = false;
            _netCts?.Dispose();
            _netCts = null;
            BtnNetTest.Content = "TESTAR";
        }
    }

    // ================= Tema (igual ao Windows / escuro / claro) =================

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        // Igual ao Windows → Escuro → Claro → Igual ao Windows
        ThemeManager.SetMode(ThemeManager.Mode switch { ThemeManager.System => ThemeManager.Dark, ThemeManager.Dark => ThemeManager.Light, _ => ThemeManager.System });
        _settings.ThemeMode = ThemeManager.Mode;
        _settings.LightTheme = ThemeManager.IsLight;
        try { _settings.Save(); } catch { }
    }

    /// <summary>Paleta trocada (pelo botão ou pelo Windows): moldura nativa, botão e logo acompanham.</summary>
    private void OnThemeChanged()
    {
        UpdateThemeButton();
        SyncSettingsTheme();
        UI.Services.WindowEffects.Apply(this, ThemeManager.IsLight);
    }

    /// <summary>Mostra o ícone do modo atual (monitor = igual ao Windows, lua = escuro, sol = claro) e troca o logo.</summary>
    private void UpdateThemeButton()
    {
        bool light = ThemeManager.IsLight;
        (BtnTheme.Content, BtnTheme.ToolTip) = ThemeManager.Mode switch
        {
            ThemeManager.System => ("", $"Tema: igual ao Windows (agora {(light ? "claro" : "escuro")}). Clique para fixar o escuro"),
            ThemeManager.Dark => ("", "Tema: escuro. Clique para o claro"),
            _ => ("", "Tema: claro. Clique para seguir o Windows"),
        };
        // O robô funciona nos dois temas; o "THG" tem letras brancas no escuro e escuras no claro.
        TitleWordmark.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(light ? "pack://application:,,,/Assets/thg-wordmark-dark.png" : "pack://application:,,,/Assets/thg-wordmark.png"));
    }
}
