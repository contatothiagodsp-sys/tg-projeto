using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ThgOptimization;

/// <summary>Menu lateral: categorias lembradas entre aberturas, notas do TURBO e do SHIELD ao lado do nome e versão no rodapé.</summary>
public partial class MainWindow
{
    private void InitNavExtras()
    {
        SideVersion.Text = $"tHg Optimization {AppUpdater.Current}";

        // O app fechou sozinho desde a última vez? Oferece mandar o relatório (o usuário revisa antes de enviar).
        try
        {
            var crash = new System.IO.FileInfo(App.CrashLogPath);
            // Primeira vez nesta versão: o que a versão anterior já tinha avisado conta como visto.
            if (_settings.CrashLogSeen == 0 && crash.Exists && _settings.LastCrashSeen is DateTime seen && crash.LastWriteTime <= seen)
                _settings.CrashLogSeen = crash.Length;
            if (Infrastructure.CrashReporter.TakeNew(_settings) is string text)
                Dispatcher.BeginInvoke(() => OfferCrashReport(text), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
        await Infrastructure.UiGuard.RunAsync("medição", ResumeMeasurementAsync, ex => Toast("Medição", "Não foi possível continuar: " + Err(ex), ""));
        if (_checks.Count == 0) await RunTurboAuditAsync();
        await Task.Delay(2000);
        if (_secChecks.Count == 0) await RunShieldAuditAsync();
    }

    /// <summary>
    /// Pergunta se o usuário quer mandar o relatório da falha. Sim = copia o texto e abre a página "nova issue" (curta, pelo
    /// Explorer do usuário: o navegador não herda o administrador do tHg). O usuário cola, revisa e só envia se quiser.
    /// </summary>
    private void OfferCrashReport(string text)
    {
        var report = Infrastructure.CrashReporter.Prepare(text);
        if (report == null)
        {
            Toast("O tHg fechou com um erro da última vez", $"O motivo foi salvo em {App.CrashLogPath}", "");
            return;
        }
        var r = ThgDialog.Show(this,
            "O tHg fechou com um erro da última vez.\n\nQuer enviar o relatório para corrigirmos? O tHg copia o texto do erro " +
            "(sem seu nome de usuário, a pasta do seu perfil e o nome do PC) e abre uma página do GitHub. Cole com Ctrl+V, " +
            "revise e só envie se quiser.\n\n" +
            $"O relatório também fica salvo em {App.CrashLogPath}.",
            "tHg Optimization · relatório de falha", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        bool copied = true;
        try { Clipboard.SetText(report.Text); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            copied = false; // outro programa segurando a área de transferência
            Infrastructure.AppLog.Warn("falha", ex);
        }
        var opened = Unelevated.OpenUrl(report.Url);
        if (!opened.Started)
            Toast("Relatório de falha", $"A página não abriu ({opened.Error}). O relatório está em {App.CrashLogPath}.", "");
        else if (!copied)
            Toast("Relatório de falha", $"Não foi possível copiar o texto. Abra {App.CrashLogPath} e cole o conteúdo na página.", "");
    }
}
