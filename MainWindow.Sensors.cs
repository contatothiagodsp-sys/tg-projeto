using System.Windows;

namespace ThgOptimization;

/// <summary>Meu PC → Sensores.</summary>
public partial class MainWindow
{
    /// <summary>A detecção do hardware falhou: a mensagem do erro fica na tela, em vez de voltar para "Detectando".</summary>
    private bool _sensorOpenFailed;

    private void UpdateSensorStatus()
    {
        if (_sensorOpenFailed) return;
        string status = SensorStatus.Describe(_monitor.IsOpen, _monitor.Rows.Count, DateTime.Now);
        if (StatusText.Text != status) StatusText.Text = status;
        BtnSensorDiag.Visibility = _monitor.IsOpen && _monitor.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnSensorDiag_Click(object sender, RoutedEventArgs e) => ThgDialog.Info("Sensores", BuildSensorDiagnosticText());

    private string BuildSensorDiagnosticText()
    {
        static string YesNo(bool on) => on ? "Sim" : "Não";
        string diagnosis = SensorMonitor.Explain(_monitor.DiagnoseCpuSensors());
        string library = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version?.ToString(3) ?? "desconhecida";
        return SensorStatus.DiagnosticText(YesNo(SensorMonitor.IsPawnIOInstalled), YesNo(_monitor.IsPawnIoAvailable),
            diagnosis.Length > 0 ? diagnosis : "Nenhum problema detectado na leitura da CPU.", library);
    }
}
