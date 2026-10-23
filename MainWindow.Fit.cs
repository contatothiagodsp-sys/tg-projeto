using System.Windows;
using System.Windows.Media;

namespace ThgOptimization;

/// <summary>
/// Telas pequenas: sem driver de vídeo (logo depois da DDU) o Windows roda a 1024×768 ou 800×600, menor que o tamanho
/// mínimo do app. Aí a janela ocupa a tela toda e o conteúdo inteiro é reduzido na mesma proporção, como um zoom;
/// quando a resolução volta (driver instalado), volta ao tamanho normal sozinho.
/// </summary>
public partial class MainWindow
{
    private const double DesignWidth = 1180, DesignHeight = 720;

    private void InitScreenFit()
    {
        FitToScreen();
        EventHandler onChange = (_, _) => Dispatcher.BeginInvoke(FitToScreen);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += onChange;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= onChange;
    }

    private bool _smallScreenWarned;

    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        double scale = Math.Min(1, Math.Min(area.Width / DesignWidth, area.Height / DesignHeight));
        if (scale < 0.98)
        {
            // O conteúdo é desenhado no tamanho de projeto e reduzido para caber.
            MinWidth = Math.Min(area.Width, 640);
            MinHeight = Math.Min(area.Height, 400);
            Root.LayoutTransform = new ScaleTransform(scale, scale);
            if (WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
            if (!_smallScreenWarned)
            {
                _smallScreenWarned = true;
                Dispatcher.BeginInvoke(() => Toast("Tela em resolução baixa",
                    $"{area.Width:0}×{area.Height:0}: o tHg reduziu a interface para caber. Sem driver de vídeo é normal; instale o driver e tudo volta ao tamanho certo.",
                    "\uE7F4"), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        else
        {
            Root.LayoutTransform = Transform.Identity;
            MinWidth = 1120; MinHeight = 660;
        }
    }
}
