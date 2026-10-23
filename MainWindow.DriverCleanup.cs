using System.Windows;
using System.Windows.Threading;

namespace ThgOptimization;

/// <summary>
/// Acompanha o instalador do driver baixado: na abertura, se a placa está sem driver e há um instalador pronto,
/// oferece instalar; depois que o driver é instalado, apaga o instalador e as pastas extraídas (C:\AMD, C:\NVIDIA).
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _drvWatch;
    private bool _pendingOffered;

    private void StartDriverWatch()
    {
        _ = CheckPendingDriverAsync(offerInstall: true);
        _drvWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _drvWatch.Tick += async (_, _) => { if (_settings.PendingDriver != null) await CheckPendingDriverAsync(offerInstall: false); };
        _drvWatch.Start();
    }

    private async Task CheckPendingDriverAsync(bool offerInstall)
    {
        if (_settings.PendingDriver == null) return;
        List<GpuDriverStatus> gpus;
        try { gpus = await Task.Run(DriverManager.ReadGpus); } catch { return; }

        long freed = await Task.Run(() => DriverInstallers.CleanupIfInstalled(_settings, gpus));
        if (freed > 0)
        {
            Toast("Driver instalado", $"Instalador e arquivos extraídos apagados: {freed / 1073741824.0:0.0} GB liberados.", "\uE74D");
            if (PageDdu.IsVisible) InitDdu();
            return;
        }

        if (offerInstall && !_pendingOffered && DriverInstallers.Ready(_settings) is { } p && gpus.Any(g => g.Vendor == p.Vendor && g.NoDriver))
        {
            _pendingOffered = true;
            if (ThgDialog.Confirm("Driver de vídeo pronto para instalar",
                    $"A placa está sem driver, e o {p.Vendor} {p.Version} baixado em {p.Downloaded:dd/MM HH:mm} está pronto.\n\n" +
                    "Depois da instalação, o tHg apaga o instalador e os arquivos extraídos sozinho.",
                    "Instalar agora", "Depois", MessageBoxImage.Information))
            {
                string err = DriverInstallers.Launch(_settings);
                if (err.Length > 0) ThgDialog.Info("Driver de vídeo", err, MessageBoxImage.Warning);
            }
        }
    }

    private async void BtnDduCleanInstallers_Click(object sender, RoutedEventArgs e)
    {
        var dirs = DriverInstallers.Leftovers();
        long size = await Task.Run(() => DriverInstallers.Size(dirs));
        if (dirs.Count == 0 || size == 0) { DduLog.Text += "\nNenhum instalador de driver ocupando espaço."; return; }
        if (await Task.Run(DriverInstallers.InstallerRunning))
        {
            ThgDialog.Info("Instaladores de driver", "Um instalador de driver está aberto agora. Termine a instalação e tente de novo.", MessageBoxImage.Warning);
            return;
        }
        if (!ThgDialog.Confirm("Apagar instaladores de driver",
                $"{size / 1073741824.0:0.00} GB em:\n" + string.Join("\n", dirs.Select(d => "  • " + d)) +
                "\n\nSão instaladores já baixados e arquivos que os instaladores da AMD e da NVIDIA extraem e não usam mais.",
                "Apagar", "Cancelar")) return;
        long freed = await Task.Run(() => DriverInstallers.Clean(_settings));
        DduLog.Text += $"\n{freed / 1048576.0:N0} MB de instaladores de driver apagados.";
        InitDdu();
    }
}
