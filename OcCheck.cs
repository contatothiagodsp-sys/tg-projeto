using System.Diagnostics.Eventing.Reader;
using System.Management;

namespace ThgOptimization;

/// <summary>Apoio da aba OC: procura erros de hardware no registro do Windows e identifica a placa-mãe.</summary>
public static class OcCheck
{
    /// <summary>Critérios do registro de eventos: os mesmos na contagem da página e no vigia do overclock.</summary>
    internal const string WheaSelector = "Provider[@Name='Microsoft-Windows-WHEA-Logger']";
    internal const string GpuResetSelector = "((Provider[@Name='nvlddmkm'] and (EventID=13 or EventID=153)) or (Provider[@Name='Display'] and EventID=4101) or Provider[@Name='amdkmdag'])";

    /// <summary>Conta erros WHEA (CPU/RAM/PCIe) e reinícios do driver de vídeo desde o instante informado.</summary>
    public static HardwareErrors ErrorsSince(DateTime since)
    {
        string t = since.ToUniversalTime().ToString("o");
        try
        {
            int whea = Count($"*[System[{WheaSelector} and TimeCreated[@SystemTime>='{t}']]]", out string? lastWhea);
            int gpu = Count($"*[System[{GpuResetSelector} and TimeCreated[@SystemTime>='{t}']]]", out string? lastGpu);
            return new HardwareErrors(whea, gpu, lastWhea ?? lastGpu, null);
        }
        catch (Exception ex)
        {
            return new HardwareErrors(0, 0, null, ex.Message);
        }
    }

    private static int Count(string xpath, out string? last)
    {
        last = null;
        int n = 0;
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
        for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
        {
            using (rec)
            {
                if (n == 0)
                {
                    string text;
                    try { text = rec.FormatDescription() ?? ""; } catch { text = ""; }
                    last = $"{rec.TimeCreated:dd/MM HH:mm} · {rec.ProviderName} {rec.Id}"
                         + (text.Length > 0 ? " · " + text.Split('\n')[0].Trim() : "");
                }
                n++;
            }
        }
        return n;
    }

    public static (string Maker, string Model) Motherboard()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using (var results = s.Get())
            foreach (ManagementObject mo in results)
            using (mo)
                return (mo["Manufacturer"]?.ToString()?.Trim() ?? "", mo["Product"]?.ToString()?.Trim() ?? "");
        }
        catch { }
        return ("", "");
    }
}
