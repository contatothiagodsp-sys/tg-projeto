using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThgOptimization.Core;

namespace ThgOptimization.UI.ViewModels;

/// <summary>
/// Meu PC → Processador. A ficha é lida uma vez (WMI/registro, em segundo plano); o painel ao vivo é atualizado pelo
/// timer de sensores da janela (Refresh); plano de energia e core parking (powercfg + WMI) a cada 5 s, fora da janela.
/// </summary>
public sealed partial class CpuViewModel : ObservableObject
{
    private readonly ICpuInfoProvider _cpu;
    private readonly INavigationService _nav;
    private DateTime _powerAt;
    private bool _powerBusy;

    public CpuViewModel(ICpuInfoProvider cpu, INavigationService nav) { _cpu = cpu; _nav = nav; }

    [ObservableProperty] private string _name = "Lendo…";
    [ObservableProperty] private string _vendorBadge = "";
    [ObservableProperty] private string? _sensorWarning;
    [ObservableProperty] private IReadOnlyList<InfoRow> _staticRows = Array.Empty<InfoRow>();
    [ObservableProperty] private IReadOnlyList<InfoRow> _liveRows = Array.Empty<InfoRow>();
    [ObservableProperty] private IReadOnlyList<CpuCoreRow> _cores = Array.Empty<CpuCoreRow>();
    [ObservableProperty] private string _plan = "—";
    [ObservableProperty] private string _parkingSetting = "—";
    [ObservableProperty] private string _parkingNow = "—";

    public bool IsLoaded { get; private set; }

    /// <summary>Primeira abertura da aba: lê a ficha (uma vez) e já mostra o ao vivo.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        Refresh();
        if (IsLoaded) return;
        IsLoaded = true;
        try
        {
            var info = await Task.Run(_cpu.ReadStatic);
            Name = info.Name;
            VendorBadge = info.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                        : info.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "INTEL" : "";
            StaticRows = info.Rows;
        }
        catch (Exception ex) { Name = "Não consegui ler o processador: " + ex.Message; }
    }

    /// <summary>Chamado a cada leitura de sensores enquanto a aba está visível.</summary>
    public async void Refresh()
    {
        LiveRows = _cpu.ReadLive();
        Cores = _cpu.ReadCores();
        SensorWarning = _cpu.SensorProblem();

        if (_powerBusy || (DateTime.Now - _powerAt).TotalSeconds < 5) return;
        _powerBusy = true;
        try
        {
            var p = await Task.Run(_cpu.ReadPower);
            Plan = p.Plan;
            ParkingSetting = p.ParkingSetting;
            ParkingNow = p.ParkedNow is var (parked, total)
                ? $"Agora: {parked} de {total} processadores lógicos estacionados (contador Parking Status do Windows)"
                : "Agora: — (o contador de estacionamento não está disponível neste Windows)";
        }
        catch { }
        finally { _powerBusy = false; _powerAt = DateTime.Now; }
    }

    [RelayCommand] private void GoBench() => _nav.NavigateTo("PageBench");
    [RelayCommand] private void GoStress() => _nav.NavigateTo("PageStress");
    /// <summary>Core parking e plano de energia ficam em Otimizar → Ajustes.</summary>
    [RelayCommand] private void GoCores() => _nav.NavigateTo("PageOptimize");
}
