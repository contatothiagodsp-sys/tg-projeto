using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ThgOptimization.Core;
using ThgOptimization.Infrastructure;
using ThgOptimization.UI.Services;
using ThgOptimization.UI.ViewModels;

namespace ThgOptimization;

public partial class App : Application
{
    /// <summary>Contêiner de serviços do app (montado em OnStartup, só para a janela principal).</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Todos os serviços e ViewModels registrados num lugar só. Singletons: há uma janela e um conjunto de sensores.</summary>
    private static ServiceProvider BuildServices()
    {
        var s = new ServiceCollection();
        s.AddSingleton(_ => AppSettings.Load());
        s.AddSingleton<SensorMonitor>();
        s.AddSingleton<NavigationService>();
        s.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        s.AddSingleton<ICpuInfoProvider, WindowsCpuInfoProvider>();
        s.AddSingleton<CpuViewModel>();
        s.AddSingleton<MainWindow>();
        return s.BuildServiceProvider();
    }

    // Uma única instância: abrir o tHg Optimization de novo só traz a janela que já está aberta para a frente.
    private static Mutex? _single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    private const string ShowEventName = @"Local\ThgOptimization.Show";

    /// <summary>Uma segunda abertura do app só sinaliza; esta instância mostra a janela (mesmo que esteja na bandeja).</summary>
    private static void ListenForShowRequests(MainWindow main)
    {
        EventWaitHandle ev;
        try { ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); } catch { return; }
        var t = new Thread(() =>
        {
            while (ev.WaitOne())
                main.Dispatcher.InvokeAsync(main.ShowFromTray);
        }) { IsBackground = true, Name = "tHg show" };
        t.Start();
    }

    public static string CrashLogPath => CrashReporter.LogPath;

    private static void CrashLog(Exception? ex, string where)
    {
        CrashReporter.Log(ex, where);
    }

    /// <summary>Reabre o app (ex.: depois de instalar o driver de sensores). A nova instância espera esta fechar.</summary>
    public static void Restart()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"--after {Environment.ProcessId}")
            { UseShellExecute = true });
        }
        catch { return; }
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Registro de falhas: se o app fechar sozinho, o motivo fica em %AppData%\tHgOptimization\falhas.txt.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => CrashLog(ev.ExceptionObject as Exception, "processo");
        DispatcherUnhandledException += (_, ev) => CrashLog(ev.Exception, "janela");
        TaskScheduler.UnobservedTaskException += (_, ev) => { CrashLog(ev.Exception, "tarefa"); ev.SetObserved(); };

        // Processo separado da DDU: sem janela e sem o "uma instância só" (a janela principal continua aberta).
        if (e.Args.Contains("--ddu-worker"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = DduCleaner.WorkerMain(e.Args.Contains("--finish"));
            Shutdown(code);
            return;
        }

        // Reaberto por Restart(): espera a instância anterior fechar antes de pegar o "uma instância só".
        int at = Array.IndexOf(e.Args, "--after");
        if (at >= 0 && at + 1 < e.Args.Length && int.TryParse(e.Args[at + 1], out int pid))
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch { }

        bool first;
        try { _single = new Mutex(true, @"Local\ThgOptimization.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { first = false; } // criado por outra instância com outro nível de permissão

        if (!first)
        {
            // Já aberto (talvez escondido na bandeja): pede para a instância aberta mostrar a janela.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) { show.Set(); Shutdown(); return; }
            }
            catch { }
            IntPtr h = FindWindow(null, "tHg Optimization");
            if (h != IntPtr.Zero)
            {
                if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
                SetForegroundWindow(h);
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);
        // Aberto pelo agendamento do DDU no Modo de Segurança: só a janela de limpeza.
        if (e.Args.Contains("--ddu") || (DduCleaner.InSafeMode && DduCleaner.Pending(AppSettings.Load()) != null))
        {
            // Sem a placa de vídeo para desenhar: a janela não cai quando o driver de vídeo sai.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            new DduRunWindow().Show();
            return;
        }
        var services = BuildServices();
        Services = services;
        Exit += (_, _) => services.Dispose();
        var main = services.GetRequiredService<MainWindow>();
        MainWindow = main;
        ListenForShowRequests(main);
        if (e.Args.Contains("--after-update")) main.AnnounceUpdated();
        else if (e.Args.Contains("--tray")) main.StartInTray();
        else main.Show();
    }
}
