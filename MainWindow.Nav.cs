using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ThgOptimization;

/// <summary>Menu lateral: categorias lembradas entre aberturas, notas do TURBO e do SHIELD ao lado do nome e versão no rodapé.</summary>
public partial class MainWindow
{
    private void InitNavExtras()
    {
        var v = typeof(MainWindow).Assembly.GetName().Version;
        SideVersion.Text = v != null ? $"tHg Optimization {v.Major}.{v.Minor}{(v.Build > 0 ? $".{v.Build}" : "")}" : "tHg Optimization";

        // O app fechou sozinho desde a última vez? Mostra onde está o motivo.
        try
        {
            var crash = new System.IO.FileInfo(App.CrashLogPath);
            if (crash.Exists && crash.LastWriteTime > (_settings.LastCrashSeen ?? DateTime.MinValue))
            {
                _settings.LastCrashSeen = crash.LastWriteTime; _settings.Save();
                Dispatcher.BeginInvoke(() => Toast("O tHg fechou com um erro da última vez", $"O motivo foi salvo em {App.CrashLogPath}", "\uE7BA"),
                                       System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch { }

        foreach (var g in new ToggleButton[] { GrpExplode, GrpGames, GrpPerf, GrpTools, GrpMaker })
        {
            if (_settings.OpenNavGroups.Contains(g.Name)) g.IsChecked = true;
            g.Checked += (_, _) => SaveNavGroups();
            g.Unchecked += (_, _) => SaveNavGroups();
        }
    }

    private void SaveNavGroups()
    {
        _settings.OpenNavGroups = new[] { GrpExplode, GrpGames, GrpPerf, GrpTools, GrpMaker }
            .Where(g => g.IsChecked == true).Select(g => g.Name).ToList();
        _settings.Save();
    }

    /// <summary>Nota colorida ao lado do item do menu (verde 90+, amarelo 60+, vermelho abaixo).</summary>
    private static void SetNavScore(Border pill, TextBlock text, int score)
    {
        pill.Visibility = Visibility.Visible;
        text.Text = score.ToString();
        string tone = score >= 90 ? "Good" : score >= 60 ? "Warn" : "Bad";
        text.SetResourceReference(TextBlock.ForegroundProperty, tone);
        pill.SetResourceReference(Border.BackgroundProperty, tone + "Soft");
    }

    /// <summary>Calcula as notas do TURBO e do SHIELD em segundo plano depois que o hardware foi lido, para o menu já mostrar.</summary>
    private async void StartBackgroundAudits()
    {
        await Task.Delay(3000);
        // Aberto com o Windows (na bandeja): espera a janela abrir, ou 3 minutos, para não pesar a entrada no sistema.
        var started = DateTime.Now;
        while (!IsVisible && (DateTime.Now - started).TotalMinutes < 3) await Task.Delay(5000);
        if (_checks.Count == 0) await RunTurboAuditAsync();
        await Task.Delay(2000);
        if (_secChecks.Count == 0) await RunShieldAuditAsync();
    }
}
