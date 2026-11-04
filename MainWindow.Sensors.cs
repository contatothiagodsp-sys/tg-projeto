using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ThgOptimization.UI.ViewModels;

namespace ThgOptimization;

/// <summary>Meu PC → Sensores. O estado da página fica em <see cref="SensorPageViewModel"/>; aqui, só o que é da janela.</summary>
public partial class MainWindow
{
    private readonly SensorPageViewModel _sensorVm = new();
    private bool _sensorPageInited;
    /// <summary>A detecção do hardware falhou: a mensagem fica na tela, em vez de voltar para "Detectando".</summary>
    private string? _sensorOpenError;
    /// <summary>O aviso que o usuário dispensou. Outro aviso, com outro motivo, aparece normalmente.</summary>
    private string? _sensorDismissedBanner;
    private int _sensorChipsVersion;
    private IReadOnlyList<string>? _sensorChipHardware, _sensorChipCategories;

    private void InitSensorsPage()
    {
        if (_sensorPageInited) return;
        _sensorPageInited = true;
        SensorPinnedCards.ItemsSource = _sensorVm.Pinned;
        SensorGroups.ItemsSource = _sensorVm.Groups;
        _sensorVm.Pinned.CollectionChanged += (_, _) => UpdateSensorPinnedVisibility();
        UpdateSensorPinnedVisibility();
        BuildSensorShowChips();
        ShowSensorStatus();
    }

    private void UpdateSensorPinnedVisibility()
    {
        bool any = _sensorVm.Pinned.Count > 0;
        SensorPinnedCards.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SensorPinnedEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Página aberta ou leitura nova. Página fechada não gasta nada: ela se atualiza ao abrir.</summary>
    private void RefreshSensorsPage()
    {
        _sensorVm.SetMonitorState(_monitor.IsOpen, _sensorOpenError);
        if (_sensorVm.TotalCount != _monitor.Rows.Count) _sensorVm.Rebuild(_monitor, _settings);
        else if (_monitor.Rows.Count > 0) _sensorVm.Refresh(_monitor);
        BuildSensorFilterChips();
        ShowSensorStatus();
    }

    private void ShowSensorStatus()
    {
        string status = _sensorVm.StatusLine;
        if (SensorStatusLine.Text != status) SensorStatusLine.Text = status;
        var diag = _sensorVm.NeedsDiagnostic ? Visibility.Visible : Visibility.Collapsed;
        if (BtnSensorDiag.Visibility != diag) BtnSensorDiag.Visibility = diag;
        AutomationProperties.SetName(SensorGroups, _sensorVm.AccessibilitySummary);
    }

    /// <summary>Os chips só mudam quando a lista de hardware ou de tipos muda: recriar a cada tique faria o filtro escolhido piscar.</summary>
    private void BuildSensorFilterChips()
    {
        if (ReferenceEquals(_sensorChipHardware, _sensorVm.HardwareFilters) && ReferenceEquals(_sensorChipCategories, _sensorVm.CategoryFilters)) return;
        bool same = _sensorChipHardware != null && _sensorChipCategories != null
                    && _sensorChipHardware.SequenceEqual(_sensorVm.HardwareFilters) && _sensorChipCategories.SequenceEqual(_sensorVm.CategoryFilters);
        _sensorChipHardware = _sensorVm.HardwareFilters;
        _sensorChipCategories = _sensorVm.CategoryFilters;
        if (same) return;

        int version = ++_sensorChipsVersion; // grupo novo: os RadioButtons antigos não disputam a seleção com os novos
        FillSensorChips(SensorHardwareChips, $"SensorHw{version}", _sensorVm.HardwareFilters, _sensorVm.HardwareFilter, v => _sensorVm.HardwareFilter = v);
        FillSensorChips(SensorCategoryChips, $"SensorCat{version}", _sensorVm.CategoryFilters, _sensorVm.CategoryFilter, v => _sensorVm.CategoryFilter = v);
    }

    private void FillSensorChips(Panel panel, string group, IReadOnlyList<string> values, string? selected, Action<string?> choose)
    {
        panel.Children.Clear();
        panel.Children.Add(SensorChip(SensorPageViewModel.AllFilters, group, selected == null || !values.Contains(selected), () => choose(null)));
        foreach (string value in values)
            panel.Children.Add(SensorChip(value, group, value == selected, () => choose(value)));
    }

    private void BuildSensorShowChips()
    {
        SensorShowChips.Children.Add(SensorChip(SensorPageViewModel.AllFilters, "SensorShow", true, () => { _sensorVm.OnlyPinned = false; _sensorVm.OnlyUnavailable = false; ShowSensorStatus(); }));
        SensorShowChips.Children.Add(SensorChip("Favoritos", "SensorShow", false, () => { _sensorVm.OnlyUnavailable = false; _sensorVm.OnlyPinned = true; ShowSensorStatus(); }));
        SensorShowChips.Children.Add(SensorChip("Indisponíveis", "SensorShow", false, () => { _sensorVm.OnlyPinned = false; _sensorVm.OnlyUnavailable = true; ShowSensorStatus(); }));
    }

    private RadioButton SensorChip(string label, string group, bool isChecked, Action chosen)
    {
        var chip = new RadioButton { Content = label, GroupName = group, Style = (Style)FindResource("Chip"), IsChecked = isChecked };
        chip.Checked += (_, _) => { chosen(); ShowSensorStatus(); };
        return chip;
    }

    private void SensorSearch_Changed(object sender, TextChangedEventArgs e)
    {
        _sensorVm.Search = SensorSearch.Text.Trim();
        ShowSensorStatus();
    }

    private void SensorPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        _sensorVm.TogglePin(id, _settings);
        try { _settings.Save(); }
        catch (Exception ex) { Infrastructure.AppLog.Warn("sensores", ex); } // o favorito vale nesta sessão; a próxima gravação tenta de novo
    }

    /// <summary>Texto vazio esconde. O mesmo texto que o usuário dispensou continua escondido.</summary>
    private void ShowSensorBanner(string? banner)
    {
        bool show = !string.IsNullOrEmpty(banner) && banner != _sensorDismissedBanner;
        SensorDiagBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && SensorDiagText.Text != banner) SensorDiagText.Text = banner;
    }

    private void SensorDiagDismiss_Click(object sender, RoutedEventArgs e)
    {
        _sensorDismissedBanner = SensorDiagText.Text;
        SensorDiagBanner.Visibility = Visibility.Collapsed;
    }

    private void SensorDiagRun_Click(object sender, RoutedEventArgs e) => ThgDialog.Info("Sensores", BuildSensorDiagnosticText());

    private string BuildSensorDiagnosticText()
    {
        static string YesNo(bool on) => on ? "Sim" : "Não";
        string diagnosis = SensorMonitor.Explain(_monitor.DiagnoseCpuSensors());
        string library = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version?.ToString(3) ?? "desconhecida";
        return SensorStatus.DiagnosticText(YesNo(SensorMonitor.IsPawnIOInstalled), YesNo(_monitor.IsPawnIoAvailable),
            diagnosis.Length > 0 ? diagnosis : "Nenhum problema detectado na leitura da CPU.", library);
    }
}
