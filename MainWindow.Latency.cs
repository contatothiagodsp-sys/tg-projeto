using System.Windows;
using System.Windows.Controls;

namespace ThgOptimization;

/// <summary>DESEMPENHO · Teste de engasgos: latência de DPC/ISR por driver.</summary>
public partial class MainWindow
{
    private async void BtnLatency_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        await Infrastructure.UiGuard.RunButtonAsync(BtnLatency, "latencia", async () =>
        {
            LatList.ItemsSource = null;
            LatMax.Text = "…";
            var r = await Task.Run(() => LatencyTest.Run(60, t => Dispatcher.Invoke(() => LatStatus.Text = t), CancellationToken.None));
            if (r.Error == null) { _settings.LastLatency = r; _settings.Save(); }
            ShowLatency(r);
        }, ex => { LatMax.Text = "—"; LatStatus.Text = "O teste falhou: " + Err(ex); });
    }

    private void ShowLatency(LatencyResult? r)
    {
        if (r == null) { LatStatus.Text = "Nenhum teste feito ainda."; return; }
        if (r.Error != null) { LatMax.Text = "—"; LatVerdict.Text = r.Error; LatStatus.Text = ""; return; }
        LatMax.Text = $"{r.MaxUs:N0}";
        LatMax.SetResourceReference(TextBlock.ForegroundProperty, r.Level switch { "RUIM" => "Bad", "ATENÇÃO" => "Warn", _ => "Good" });
        LatVerdict.Text = r.Verdict;
        LatStatus.Text = $"Teste de {r.When:dd/MM HH:mm} · {r.Seconds:0} s · {r.Events:N0} chamadas de driver medidas";
        LatList.ItemsSource = r.Drivers;
    }
}
