using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace ThgOptimization.Infrastructure;

/// <summary>O que foi encontrado no último scan de hardware (fica salvo nas configurações para a próxima abertura já esconder).</summary>
public sealed record HardwareCaps
{
    public bool HasCompatibleLcd { get; init; }
    public bool HasCompatibleKeyboard { get; init; }
    public bool HasCorsairDevice { get; init; }
    public bool HasOpenRgb { get; init; }
    public bool HasRazerChroma { get; init; }
    public bool HasBattery { get; init; }
    public bool HasMakerHardware { get; init; }
    public bool IsAmdGpu { get; init; }
    public bool IsNvidiaGpu { get; init; }
    public bool IsAmdCpu { get; init; }
    public bool IsX3dCpu { get; init; }
    public DateTime ScannedAt { get; init; }

    /// <summary>Algum sistema de iluminação/periférico que a página Iluminação RGB controla.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRgbPeripheral => HasCompatibleKeyboard || HasCorsairDevice || HasOpenRgb || HasRazerChroma;

    /// <summary>Resumo para o aviso do "Reescanear": o que foi achado e o que ficou de fora.</summary>
    public string Summary()
    {
        var found = new List<string>();
        if (HasCompatibleLcd) found.Add("tela LCD");
        if (HasCompatibleKeyboard) found.Add("teclado AULA HE");
        if (HasCorsairDevice) found.Add("Corsair");
        if (HasOpenRgb) found.Add("OpenRGB");
        if (HasRazerChroma) found.Add("Razer Chroma");
        if (HasMakerHardware) found.Add("placa Arduino/serial");
        if (HasBattery) found.Add("bateria (notebook)");
        string gpu = IsNvidiaGpu && IsAmdGpu ? "GPU NVIDIA + AMD" : IsNvidiaGpu ? "GPU NVIDIA" : IsAmdGpu ? "GPU AMD" : "GPU Intel/outra";
        string cpu = IsX3dCpu ? "Ryzen X3D" : IsAmdCpu ? "CPU AMD" : "CPU Intel";
        return $"{cpu} · {gpu} · " + (found.Count > 0 ? "encontrado: " + string.Join(", ", found) : "nenhum periférico compatível encontrado");
    }
}

/// <summary>
/// Detecção rápida do hardware no primeiro uso (e a cada abertura, em segundo plano): só lista o que o Windows já sabe
/// (VID/PID dos aparelhos USB, registro e WMI), sem abrir nenhum aparelho, porta COM ou SDK. Serve para esconder os
/// módulos cujo hardware não existe neste PC (telas LCD, teclado AULA, Corsair…).
/// </summary>
public static class HardwareCapabilities
{
    public static Task<HardwareCaps> DetectAsync() => Task.Run(Detect);

    public static HardwareCaps Detect()
    {
        List<UsbDev> usb;
        try { usb = UsbInventory.Scan(); } catch { usb = new(); }

        bool Usb(Func<UsbDev, bool> f) => usb.Any(f);
        var (amdGpu, nvGpu) = GpuVendors();
        var (amdCpu, x3d) = CpuVendor();
        return new HardwareCaps
        {
            // Telas que o tHg sabe desenhar (Lian Li TL LCD, TURZX/Turing serial e USB): as entradas da tabela com resolução.
            HasCompatibleLcd = Usb(d => d.Known is { Width: > 0 }),
            HasCompatibleKeyboard = Usb(d => ushort.TryParse(d.Vid, System.Globalization.NumberStyles.HexNumber, null, out ushort v)
                                             && ushort.TryParse(d.Pid, System.Globalization.NumberStyles.HexNumber, null, out ushort p)
                                             && AulaHeBackend.IsSupported(v, p)),
            HasCorsairDevice = Usb(d => d.Vid == "1B1C") || ProcessUtil.IsRunning("iCUE"),
            HasOpenRgb = MainWindow.FindOpenRgb() != null || ProcessUtil.IsRunning("OpenRGB") || Listening(6742),
            HasRazerChroma = File.Exists(Path.Combine(Environment.SystemDirectory, "RzChromaSDK64.dll")) || ProcessUtil.IsRunning("RazerAppEngine"),
            HasBattery = GamerAudit.Power().HasBattery,
            // Conversores USB-serial e placas Arduino; aparelhos da tabela (telas, dongles Lian Li) com o mesmo VID não contam.
            HasMakerHardware = Usb(d => d.InstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                                        && MakerDrivers.ChipByVid(d.Vid) != null && (d.Known == null || d.Pid == "7523")),
            IsAmdGpu = amdGpu, IsNvidiaGpu = nvGpu, IsAmdCpu = amdCpu, IsX3dCpu = x3d,
            ScannedAt = DateTime.Now,
        };
    }

    private static bool Listening(int port)
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port); }
        catch { return false; }
    }

    private static (bool Amd, bool Nvidia) GpuVendors()
    {
        bool amd = false, nv = false;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_VideoController");
            using var res = s.Get();
            foreach (ManagementObject o in res)
                using (o)
                {
                    string id = o["PNPDeviceID"]?.ToString() ?? "";
                    amd |= id.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase);
                    nv |= id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);
                }
        }
        catch { }
        return (amd, nv);
    }

    private static (bool Amd, bool X3d) CpuVendor()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string vendor = k?.GetValue("VendorIdentifier") as string ?? "";
            string name = k?.GetValue("ProcessorNameString") as string ?? "";
            return (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase), name.Contains("X3D", StringComparison.OrdinalIgnoreCase));
        }
        catch { return (false, false); }
    }
}
