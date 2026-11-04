using System.Windows;
using System.Windows.Threading;
using ThgOptimization.Infrastructure;

namespace ThgOptimization;

/// <summary>Configurações → O QUE O APP ESTÁ FAZENDO. Os timers continuam com quem os criou; aqui eles só ganham nome e, se puderem parar, como parar.</summary>
public partial class MainWindow
{
    private DispatcherTimer? _timerPanelTimer;
    /// <summary>"Pausar tudo": o painel continua abrindo, mas não guarda amostras novas até retomar.</summary>
    private bool _dashSamplingPaused;

    private static readonly string[] TimerNames =
    {
        "Janela e sensores", "Overlay", "Modo Jogo", "Telas LCD: envio", "Telas LCD: atualização contínua", "Efeitos de luz", "Teclado AULA: troca de perfil",
        "Driver pendente", "Dispositivos USB: espera", "Benchmark de CPU", "Painel deste cartão",
    };

    private void RegisterTimers()
    {
        TimerRegistry.Register("Janela e sensores", "Sensores", () => _timer.Interval, () => _timer.IsEnabled);
        TimerRegistry.Register("Overlay", "Jogo", () => _overlayTimer?.Interval ?? TimeSpan.Zero, () => _overlayTimer?.IsEnabled == true);
        TimerRegistry.Register("Modo Jogo", "Jogo", TimeSpan.FromSeconds(3), repeating: true, () => _gmTimer?.IsEnabled == true);
        TimerRegistry.Register("Telas LCD: envio", "Telas LCD", TimeSpan.FromMilliseconds(100), repeating: true, () => _lcdTimer?.IsEnabled == true);
        TimerRegistry.Register("Dispositivos USB: espera", "Hardware", TimeSpan.FromMilliseconds(1500), repeating: false, () => _usbDebounce?.IsEnabled == true);
        TimerRegistry.Register("Benchmark de CPU", "Bench", TimeSpan.FromMilliseconds(100), repeating: true, () => _benchTimer?.IsEnabled == true);
        TimerRegistry.Register("Painel deste cartão", "Configurações", TimeSpan.FromSeconds(2), repeating: true, () => _timerPanelTimer?.IsEnabled == true);

        TimerRegistry.RegisterPausable("Driver pendente", "Hardware", TimeSpan.FromSeconds(60), () => _drvWatch?.IsEnabled == true, () => _drvWatch?.Stop(), () => _drvWatch?.Start());
        TimerRegistry.RegisterPausable("Teclado AULA: troca de perfil", "Hardware", TimeSpan.FromSeconds(3), () => _aulaTimer?.IsEnabled == true, () => _aulaTimer?.Stop(), () => _aulaTimer?.Start());
        TimerRegistry.RegisterPausable("Telas LCD: atualização contínua", "Telas LCD", TimeSpan.FromSeconds(1), () => _lcdLoopTimer?.IsEnabled == true, () => _lcdLoopTimer?.Stop(), () => _lcdLoopTimer?.Start());
        TimerRegistry.RegisterPausable("Efeitos de luz", "Iluminação", TimeSpan.FromMilliseconds(33), () => _lightTimer?.IsEnabled == true, () => _lightTimer?.Stop(), () => _lightTimer?.Start());
        TimerRegistry.RegisterPausable("Amostras do painel", "Painel", TimeSpan.FromSeconds(1), () => !_dashSamplingPaused, () => _dashSamplingPaused = true, () => _dashSamplingPaused = false);
    }

    private void UnregisterTimers()
    {
        foreach (string name in TimerNames) TimerRegistry.Unregister(name);
        TimerRegistry.Unregister("Amostras do painel");
    }

    /// <summary>Atualiza a cada 2 s, só com a página aberta: olhar o consumo não pode virar consumo.</summary>
    private void StartTimerPanel()
    {
        UpdateTimerPanel();
        if (_timerPanelTimer == null)
        {
            _timerPanelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timerPanelTimer.Tick += (_, _) =>
            {
                if (PageSettings.IsVisible) UpdateTimerPanel();
                else _timerPanelTimer?.Stop();
            };
        }
        _timerPanelTimer.Start();
    }

    private void UpdateTimerPanel()
    {
        SetTimerList.ItemsSource = TimerRegistry.Snapshot();
        bool paused = TimerRegistry.AnyPaused;
        SetTimersPause.IsEnabled = !paused;
        SetTimersResume.IsEnabled = paused;
    }

    private void SetTimersPause_Click(object sender, RoutedEventArgs e)
    {
        int stopped = TimerRegistry.PauseNonEssential();
        SetTimersStatus.Text = stopped == 0 ? "Nada para pausar: só o essencial está rodando." : $"{stopped} tarefa(s) pausada(s). Luzes e telas LCD ficam paradas até retomar.";
        UpdateTimerPanel();
    }

    private void SetTimersResume_Click(object sender, RoutedEventArgs e)
    {
        int resumed = TimerRegistry.ResumePaused();
        SetTimersStatus.Text = $"{resumed} tarefa(s) retomada(s).";
        UpdateTimerPanel();
    }
}
