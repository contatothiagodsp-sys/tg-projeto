using System.Windows;

namespace ThgOptimization;

/// <summary>
/// Meu PC → Processador: ficha real (WMI, registro, caches do Windows), sensores ao vivo do SensorMonitor por núcleo,
/// plano de energia e core parking (configuração do plano e estado atual de cada processador lógico).
/// </summary>
public partial class MainWindow
{
    private bool _cpuPageLoaded;
    private bool _cpuPowerBusy;
    private DateTime _cpuPowerAt;

    private async void InitCpuPage()
    {
        UpdateCpuPage();
        if (_cpuPageLoaded) return;
        _cpuPageLoaded = true;
        try
        {
            var info = await Task.Run(CpuInfo.Collect);
            CpuPageName.Text = info.Name;
            CpuPageVendor.Text = info.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                               : info.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "INTEL" : "";
            CpuStatic.ItemsSource = info.Rows;
        }
        catch (Exception ex) { CpuPageName.Text = "Não consegui ler o processador: " + ex.Message; }
    }

    /// <summary>A cada segundo com a aba aberta: sensores (já lidos pelo timer); energia e parking a cada 5 s, fora da janela.</summary>
    private async void UpdateCpuPage()
    {
        CpuLiveSummary.ItemsSource = CpuInfo.Live(_monitor);
        CpuCores.ItemsSource = CpuInfo.Cores(_monitor);
        var problem = _monitor.IsOpen ? _monitor.DiagnoseCpuSensors() : SensorMonitor.CpuSensorProblem.None;
        CpuPageWarn.Visibility = problem != SensorMonitor.CpuSensorProblem.None ? Visibility.Visible : Visibility.Collapsed;
        CpuPageWarn.Text = SensorMonitor.Explain(problem);

        if (_cpuPowerBusy || (DateTime.Now - _cpuPowerAt).TotalSeconds < 5) return;
        _cpuPowerBusy = true;
        try
        {
            var (state, parked) = await Task.Run(() => (CpuInfo.PowerState(), CpuInfo.ParkedNow()));
            CpuPlan.Text = state.Plan;
            CpuParkSetting.Text = state.ParkingSetting;
            CpuParkNow.Text = parked is var (p, t)
                ? $"Agora: {p} de {t} processadores lógicos estacionados (contador Parking Status do Windows)"
                : "Agora: — (o contador de estacionamento não está disponível neste Windows)";
        }
        catch { }
        finally { _cpuPowerBusy = false; _cpuPowerAt = DateTime.Now; }
    }

    private void CpuGoBench_Click(object sender, RoutedEventArgs e) => NavigateTo("PageBench");
    private void CpuGoStress_Click(object sender, RoutedEventArgs e) => NavigateTo("PageStress");
    private void CpuGoCores_Click(object sender, RoutedEventArgs e) => NavigateTo("PageOptimize");
}
