using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThgOptimization;

/// <summary>Janela da limpeza agendada no Modo de Segurança: mostra o progresso, limpa e volta ao Windows normal.</summary>
public sealed class DduRunWindow : Window
{
    private readonly TextBlock _log = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };

    public DduRunWindow()
    {
        Title = "tHg Optimization · DDU";
        Width = 720; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(8, 8, 8));
        Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 245));
        _log.Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160));
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

        var root = new DockPanel { Margin = new Thickness(28) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        try { head.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")), Width = 26, Height = 26, Margin = new Thickness(0, 0, 12, 0) }); } catch { }
        head.Children.Add(new TextBlock { Text = "DDU · limpeza do driver de vídeo", FontSize = 14, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = _log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) => await RunAsync();
    }

    private void Log(string m) => Dispatcher.Invoke(() => _log.Text += m + "\n");

    private async Task RunAsync()
    {
        var st = AppSettings.Load();
        var job = DduCleaner.Pending(st);
        if (job == null)
        {
            _status.Text = "Nenhuma limpeza agendada.";
            if (DduCleaner.InSafeMode) { Log("Voltando ao Windows normal…"); DduCleaner.Finish(new DduOptions(false, false, false, false), st, Log); }
            return;
        }
        _status.Text = $"Removendo {DduCleaner.What(job.Vendor)}… não desligue o PC.";
        // Limpeza num processo separado: se esta janela cair quando o driver de vídeo sair, ela continua até o fim.
        int removed = 0;
        try
        {
            long pos = DduCleaner.LogLength();
            using var worker = DduCleaner.StartWorker(job.Vendor, job.Options, finish: true);
            while (!worker.HasExited)
            {
                await Task.Delay(400);
                var (lines, np) = DduCleaner.ReadLogFrom(pos); pos = np;
                foreach (var l in lines) Log(l);
            }
            foreach (var l in DduCleaner.ReadLogFrom(pos).Lines) Log(l);
            removed = worker.ExitCode;
        }
        catch (Exception ex) { Log("Erro: " + ex.Message); }
        if (removed > 0)
        {
            _status.Text = job.Options.Finish == DduFinish.Shutdown ? "Pronto. Desligando…" : "Pronto. Reiniciando no Windows normal…";
            return; // o processo separado já cuidou do reinício/desligamento
        }

        if (removed == 0)
        {
            // Nada foi removido: não reinicia às cegas. Mostra o registro e espera o usuário (ou 3 minutos).
            _status.Text = "Nada foi removido. Veja as mensagens abaixo; o PC volta ao Windows normal quando você clicar.";
            var done = new TaskCompletionSource();
            var btn = new Button { Content = "VOLTAR AO WINDOWS NORMAL", Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 14, 0, 0),
                                   HorizontalAlignment = HorizontalAlignment.Left };
            btn.Click += (_, _) => done.TrySetResult();
            ((DockPanel)Content).Children.Insert(2, btn);
            DockPanel.SetDock(btn, Dock.Bottom);
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(3)));
            DduCleaner.Finish(job.Options with { InstallerPath = null }, st, Log);
            return;
        }

        _status.Text = job.Options.Finish == DduFinish.Shutdown
            ? "Pronto. Desligando em 10 segundos: troque a placa de vídeo e ligue o PC."
            : "Pronto. Reiniciando no Windows normal em 10 segundos…";
        DduCleaner.Finish(job.Options, st, Log);
    }
}
