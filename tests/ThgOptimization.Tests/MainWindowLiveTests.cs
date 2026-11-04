using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThgOptimization.Infrastructure;
using ThgOptimization.UI.Services;
using ThgOptimization.UI.ViewModels;

namespace ThgOptimization.Tests;

/// <summary>Só roda com THG_WINDOW_TEST=1: abre uma janela de verdade na tela, e o CI não tem o que ela precisa.</summary>
internal sealed class ManualWindowFactAttribute : FactAttribute
{
    public ManualWindowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("THG_WINDOW_TEST") != "1")
            Skip = "manual: abre a janela real e lê os sensores deste PC. Ver docs/auditoria-sensores.md";
    }
}

/// <summary>
/// A janela real, com o code-behind inteiro, sem o App: dá para rodar com o tHg instalado aberto, porque a trava de uma instância fica no App. Tudo aponta para uma pasta temporária,
/// e nenhuma configuração real é lida nem gravada. Ficam de fora as páginas que falam com hardware compartilhado com o app aberto: hub de fans, telas LCD, RGB e a sessão ETW de frames.
/// </summary>
public class MainWindowLiveTests
{
    private static readonly HashSet<string> SharedHardware = new() { "PageFans", "PageLcd", "PageInteg", "PageBottleneck", "PageOverlay", "PageMakerPorts", "PageMakerDrv", "PageUsb" };
    private static readonly string[] Screenshots = { "PageSensors", "PageHistory", "PageSettings", "PageTurbo", "PageDash", "PageGpu" };

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Save(Visual visual, double width, double height, string path)
    {
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    [ManualWindowFact]
    public void Window_opens_loads_the_hardware_and_every_page_navigates()
    {
        string folder = Path.Combine(Path.GetTempPath(), "thg-janela-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string output = Environment.GetEnvironmentVariable("THG_RENDER_DIR") is { Length: > 0 } dir ? dir : folder;
        typeof(AppSettings).GetField("_folder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, folder);

        var errors = new List<string>();
        TaskScheduler.UnobservedTaskException += (_, e) => { errors.Add("tarefa: " + e.Exception); e.SetObserved(); };
        var report = new List<string>();

        XamlSmokeTests.OnSta(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, e) => { errors.Add("janela: " + e.Exception); e.Handled = true; };
            // No app, a abertura já roda dentro do Dispatcher. Sem isto, um await no construtor da janela voltaria em outra thread.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            // Aqui o processo é o do teste: as imagens da janela estão no assembly do app, e o WPF só aceita definir isto uma vez, o que já aconteceu.
            var appAssembly = typeof(MainWindow).Assembly;
            typeof(Visual).Assembly.GetType("System.Windows.Navigation.BaseUriHelper")!
                .GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, appAssembly);
            var container = typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer") ?? typeof(Visual).Assembly.GetType("MS.Internal.AppModel.ResourceContainer");
            var wrapperField = container?.GetField("_applicationResourceManagerWrapper", BindingFlags.NonPublic | BindingFlags.Static);
            if (wrapperField != null)
                wrapperField.SetValue(null, Activator.CreateInstance(wrapperField.FieldType, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { appAssembly }, null));
            XamlSmokeTests.EnsureTheme();

            // Sem atualização, sem troca de perfil no teclado, sem curvas e sem bandeja no fechamento.
            var settings = SettingsMigration.NewInstall(AppUpdater.Current.ToString());
            settings.AutoCheckUpdates = false;
            settings.AulaAutoProfile = false;
            settings.CloseToTray = false;
            settings.AutoGameMode = false;
            settings.FirstHardwareScanDone = true;

            var monitor = new SensorMonitor();
            var nav = new NavigationService();
            var cpuVm = new CpuViewModel(new WindowsCpuInfoProvider(monitor), nav);
            var window = new MainWindow(settings, monitor, nav, cpuVm) { ShowActivated = false };
            window.Show();
            Pump(TimeSpan.FromSeconds(15)); // detecção do hardware e primeiras leituras

            var navigate = typeof(MainWindow).GetMethod("NavigateTo", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (object[] item in PolishTests.Pages())
            {
                string page = (string)item[0];
                if (SharedHardware.Contains(page)) continue;
                navigate.Invoke(window, new object[] { page });
                Pump(TimeSpan.FromMilliseconds(page == "PageSensors" ? 2500 : 900));
                var element = (FrameworkElement)window.FindName(page);
                if (!element.IsVisible) errors.Add($"{page} não ficou visível.");
                report.Add($"{page}: visível={element.IsVisible}, {element.ActualWidth:0}x{element.ActualHeight:0}");
                if (Screenshots.Contains(page)) Save(window, window.ActualWidth, window.ActualHeight, Path.Combine(output, $"janela-{page}.png"));
            }

            // Sensores de novo, com favoritos e filtro: o caminho que o usuário faz.
            navigate.Invoke(window, new object[] { "PageSensors" });
            var vm = (SensorPageViewModel)typeof(MainWindow).GetField("_sensorVm", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            report.Add($"Sensores: {vm.TotalCount} linhas, {vm.Groups.Count} grupos · {vm.StatusLine}");
            foreach (string id in monitor.Rows.Where(r => r.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.Cpu or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd
                                                         && r.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature).Take(4).Select(r => r.Id).ToList())
                vm.TogglePin(id, settings);
            Pump(TimeSpan.FromSeconds(4));
            Save(window, window.ActualWidth, window.ActualHeight, Path.Combine(output, "janela-sensores-favoritos.png"));
            if (vm.TotalCount == 0) errors.Add("A página de sensores ficou sem linhas.");

            // O painel de auto-auditoria, rolado até aparecer.
            navigate.Invoke(window, new object[] { "PageSettings" });
            Pump(TimeSpan.FromSeconds(1));
            ((FrameworkElement)window.FindName("SetTimerList")).BringIntoView();
            Pump(TimeSpan.FromSeconds(3));
            Save(window, window.ActualWidth, window.ActualHeight, Path.Combine(output, "janela-painel-timers.png"));

            report.Add("Abertura: " + StartupLog.Summary);
            report.Add("Timers: " + string.Join(" | ", TimerRegistry.Snapshot().Select(e => $"{e.Area}/{e.Name} {e.IntervalText} {e.StateText}")));
            window.Close();
            Pump(TimeSpan.FromMilliseconds(500));
            dispatcher.InvokeShutdown();
        });

        string log = File.Exists(AppLog.PathOnDisk) ? File.ReadAllText(AppLog.PathOnDisk) : "";
        report.Add("--- registro ---");
        report.AddRange(log.Split('\n').Where(l => l.Contains("[sensores]") || l.Contains("[startup]") || l.Contains(" ERRO ")).Select(l => l.TrimEnd()));
        File.WriteAllLines(Path.Combine(output, "janela-relatorio.txt"), report.Concat(errors.Select(e => "FALHA " + e)));
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { /* algum registro ainda aberto: é uma pasta temporária */ }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
