using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using ThgOptimization.Core;

namespace ThgOptimization.Infrastructure;

/// <summary>ICpuInfoProvider do Windows: estáticos e energia por WMI/registro/powercfg, ao vivo pelo SensorMonitor.</summary>
public sealed class WindowsCpuInfoProvider : ICpuInfoProvider
{
    private readonly SensorMonitor _monitor;
    public WindowsCpuInfoProvider(SensorMonitor monitor) => _monitor = monitor;

    public CpuStaticInfo ReadStatic() => CpuInfo.Collect();
    public CpuPowerInfo ReadPower() => CpuInfo.PowerState();
    public IReadOnlyList<InfoRow> ReadLive() => CpuInfo.Live(_monitor);
    public IReadOnlyList<CpuCoreRow> ReadCores() => CpuInfo.Cores(_monitor);

    public string? SensorProblem()
    {
        if (!_monitor.IsOpen) return null;
        var p = _monitor.DiagnoseCpuSensors();
        return p == SensorMonitor.CpuSensorProblem.None ? null : SensorMonitor.Explain(p);
    }
}

/// <summary>
/// Dados da aba Meu PC → Processador. Estáticos: WMI (Win32_Processor), registro (CentralProcessor\0: identificador e
/// revisão do microcódigo) e caches pelo GetLogicalProcessorInformationEx. Ao vivo: sensores do LibreHardwareMonitor
/// (SensorMonitor) e o estado de estacionamento de cada processador lógico pelos contadores do Windows.
/// Nada é estimado: o que o hardware ou o Windows não informa aparece como "—".
/// </summary>
public static class CpuInfo
{
    public const string Dash = "—";

    // ---------------- estáticos ----------------

    public static CpuStaticInfo Collect()
    {
        var rows = new List<InfoRow>();
        void Add(string k, string? v) => rows.Add(new(k, string.IsNullOrWhiteSpace(v) ? Dash : v.Trim()));
        string name = Dash, vendor = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, Manufacturer, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, " +
                                                       "VirtualizationFirmwareEnabled, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject mo in s.Get())
            {
                name = mo["Name"]?.ToString()?.Trim() ?? Dash;
                vendor = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                Add("Fabricante", vendor);
                Add("Soquete", mo["SocketDesignation"]?.ToString());
                Add("Núcleos físicos", mo["NumberOfCores"]?.ToString());
                Add("Threads (lógicos)", mo["NumberOfLogicalProcessors"]?.ToString());
                Add("Clock máximo declarado (WMI)", mo["MaxClockSpeed"] is { } mhz ? $"{mhz} MHz" : null);
                Add("Virtualização na BIOS", mo["VirtualizationFirmwareEnabled"] switch { true => "Ativada", false => "Desativada", _ => null });
                break; // um soquete: os outros (servidores) repetiriam a mesma ficha
            }
        }
        catch (Exception ex) { Add("WMI", "erro: " + ex.Message); }

        var (family, model, stepping, micro) = Registry0();
        Add("Família / modelo / stepping", family != null ? $"{family} / {model} / {stepping}" : null);
        Add("Revisão do microcódigo", micro);

        foreach (var (label, value) in Caches()) Add(label, value);
        return new CpuStaticInfo(name, vendor, rows);
    }

    /// <summary>Identificador ("AMD64 Family 25 Model 33 Stepping 2") e "Update Revision" do processador 0.</summary>
    internal static (string? Family, string? Model, string? Stepping, string? Microcode) Registry0()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k == null) return (null, null, null, null);
            var m = Regex.Match(k.GetValue("Identifier")?.ToString() ?? "", @"Family (\d+) Model (\d+) Stepping (\d+)");
            string? micro = null;
            if (k.GetValue("Update Revision") is byte[] { Length: >= 4 } b)
            {
                // REG_BINARY. Na Intel são 8 bytes com a revisão nos 4 de cima; na AMD costumam ser 4 bytes (ex.: 32 40 40 0B = 0xB404032).
                uint lo = BitConverter.ToUInt32(b, 0), hi = b.Length >= 8 ? BitConverter.ToUInt32(b, 4) : 0;
                uint rev = hi != 0 ? hi : lo;
                if (rev != 0) micro = $"0x{rev:X}";
            }
            return m.Success ? (Hex(m.Groups[1].Value), Hex(m.Groups[2].Value), m.Groups[3].Value, micro) : (null, null, null, micro);
        }
        catch { return (null, null, null, null); }
        static string Hex(string dec) => int.TryParse(dec, out int v) ? $"{v} (0x{v:X})" : dec;
    }

    /// <summary>Caches reais pelo Windows (RelationCache): quantas instâncias de cada nível/tipo e o tamanho de cada uma.</summary>
    internal static unsafe List<(string, string)> Caches()
    {
        var list = new List<(string, string)>();
        uint len = 0;
        GetLogicalProcessorInformationEx(2 /* RelationCache */, null, ref len);
        if (len == 0) return list;
        var buf = new byte[len];
        var seen = new SortedDictionary<(int Level, int Type), (int Count, uint Size)>();
        fixed (byte* p = buf)
        {
            if (!GetLogicalProcessorInformationEx(2, p, ref len)) return list;
            for (uint off = 0; off < len;)
            {
                byte* e = p + off;
                int size = *(int*)(e + 4);
                if (size <= 0) break;
                // CACHE_RELATIONSHIP: Level (byte) em +8, LineSize em +10, CacheSize (uint) em +12, Type em +16.
                int level = e[8], type = *(int*)(e + 16);
                uint bytes = *(uint*)(e + 12);
                var key = (level, type);
                seen[key] = seen.TryGetValue(key, out var c) ? (c.Count + 1, bytes) : (1, bytes);
                off += (uint)size;
            }
        }
        foreach (var ((level, type), (count, bytes)) in seen)
        {
            string kind = type switch { 1 => " instruções", 2 => " dados", 3 => " trace", _ => "" };
            list.Add(($"Cache L{level}{kind}", $"{count} × {Size(bytes)}" + (count > 1 ? $" ({Size((ulong)bytes * (ulong)count)} no total)" : "")));
        }
        return list;
    }

    private static string Size(ulong b) => b >= 1 << 20 && b % (1 << 20) == 0 ? $"{b >> 20} MB" : $"{b >> 10} KB";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    // ---------------- energia ----------------

    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00", CoreParkingMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";

    /// <summary>Nome do plano ativo, o "mínimo de núcleos sem estacionar" dele (na tomada) e os núcleos estacionados agora.</summary>
    public static CpuPowerInfo PowerState()
    {
        string plan = Dash, parking = Dash;
        try
        {
            if (PowerPlan.Active() is string g)
                plan = PowerPlan.List().FirstOrDefault(p => p.Guid == g).Name is { Length: > 0 } n ? n : g;
            int? acIndex = PowerApi.ReadAc(null, SubProcessor, CoreParkingMinCores);
            if (acIndex == null)
            {
                var hex = Regex.Matches(SecurityAudit.Run("powercfg.exe", $"/qh scheme_current {SubProcessor} {CoreParkingMinCores}", 10_000).Output,
                                        @"0x([0-9a-fA-F]{8})");
                // As duas últimas linhas 0x........ são o valor na tomada (AC) e na bateria (DC).
                if (hex.Count >= 2) acIndex = Convert.ToInt32(hex[^2].Groups[1].Value, 16);
            }
            if (acIndex is int ac)
            {
                parking = ac >= 100 ? "Desligado no plano (100% dos núcleos sempre acordados)"
                                    : $"Ligado no plano (mínimo de {ac}% dos núcleos acordados)";
            }
        }
        catch { }
        return new CpuPowerInfo(plan, parking, ParkedNow());
    }

    /// <summary>
    /// Quantos processadores lógicos estão estacionados agora (contador "Parking Status" do Windows).
    /// null se o contador não existe neste Windows.
    /// </summary>
    private static (int Parked, int Total)? ParkedNow()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, ParkingStatus FROM Win32_PerfFormattedData_Counters_ProcessorInformation");
            int parked = 0, total = 0;
            foreach (ManagementObject mo in s.Get())
            {
                if (mo["Name"]?.ToString()?.Contains("_Total") != false) continue; // "_Total" e "0,_Total"
                total++;
                if (Convert.ToUInt32(mo["ParkingStatus"] ?? 0u) != 0) parked++;
            }
            return total > 0 ? (parked, total) : null;
        }
        catch { return null; }
    }

    // ---------------- ao vivo (sensores) ----------------

    private static readonly Regex CoreClock = new(@"^(?:CPU )?Core #(\d+)$", RegexOptions.Compiled);
    private static readonly Regex CoreLoad = new(@"^CPU Core #(\d+)(?: Thread #(\d+))?$", RegexOptions.Compiled);

    public static string Fmt(float? v, string fmt, string unit) => v is float f ? f.ToString(fmt) + unit : Dash;

    /// <summary>Resumo ao vivo: temperatura, consumo, tensões e carga total da CPU.</summary>
    public static List<InfoRow> Live(SensorMonitor m)
    {
        var cpu = m.Rows.Where(r => r.HardwareType == HardwareType.Cpu).ToList();
        SensorRow? Find(SensorType t, Func<string, bool> name) => cpu.FirstOrDefault(r => r.Type == t && r.RawValue != null && name(r.Name));
        var rows = new List<InfoRow>
        {
            new("Temperatura (pacote / Tctl)", Fmt(m.CpuTemperature, "0.#", " °C")),
        };
        foreach (var ccd in cpu.Where(r => r.Type == SensorType.Temperature && r.Name.Contains("CCD") && r.RawValue is > 0))
            rows.Add(new("Temperatura " + ccd.Name, ccd.Value));
        rows.Add(new("Consumo do pacote", Fmt(m.CpuPower, "0.#", " W")));
        var vid = Find(SensorType.Voltage, n => n.Contains("VID") && !n.Contains('#'));
        if (vid != null) rows.Add(new("Core VID", vid.Value));
        else
        {
            // Só VID por núcleo: mostra o maior deles (o que a CPU está pedindo para o núcleo mais exigido).
            var perCore = cpu.Where(r => r.Type == SensorType.Voltage && r.Name.Contains("VID") && r.RawValue is > 0).MaxBy(r => r.RawValue);
            rows.Add(new("Core VID (maior entre os núcleos)", perCore?.Value ?? Dash));
        }
        // Vcore: o que a CPU informa (SVI2/SVI3 na AMD, "CPU Core" na Intel) ou, sem isso, o Super I/O da placa-mãe.
        var vcore = Find(SensorType.Voltage, n => n.StartsWith("Core (SVI") || n == "CPU Core" || n == "Vcore")
                    ?? m.Rows.FirstOrDefault(r => r.HardwareType == HardwareType.SuperIO && r.Type == SensorType.Voltage && r.RawValue is > 0
                                                  && (r.Name == "Vcore" || r.Name == "CPU Core" || r.Name == "CPU Vcore"));
        rows.Add(new("Tensão do núcleo (Vcore)", vcore?.Value ?? Dash));
        rows.Add(new("Carga total", Fmt(m.CpuLoad, "0", " %")));
        var bus = Find(SensorType.Clock, n => n.StartsWith("Bus"));
        if (bus != null) rows.Add(new("Clock do barramento", bus.Value));
        return rows;
    }

    /// <summary>Uma linha por núcleo físico, na ordem dos sensores: clock, temperatura (se o sensor existe) e carga por thread.</summary>
    public static List<CpuCoreRow> Cores(SensorMonitor m)
    {
        var cores = new SortedDictionary<int, (string Clock, string Temp, SortedDictionary<int, string> Loads)>();
        (string, string, SortedDictionary<int, string>) Get(int n) =>
            cores.TryGetValue(n, out var c) ? c : cores[n] = (Dash, Dash, new SortedDictionary<int, string>());
        foreach (var r in m.Rows.Where(r => r.HardwareType == HardwareType.Cpu))
        {
            switch (r.Type)
            {
                case SensorType.Clock when CoreClock.Match(r.Name) is { Success: true } cm:
                {
                    int n = int.Parse(cm.Groups[1].Value);
                    var c = Get(n); cores[n] = (r.RawValue != null ? r.Value : Dash, c.Item2, c.Item3);
                    break;
                }
                case SensorType.Temperature when CoreClock.Match(r.Name) is { Success: true } tm:
                {
                    int n = int.Parse(tm.Groups[1].Value);
                    var c = Get(n); cores[n] = (c.Item1, r.RawValue != null ? r.Value : Dash, c.Item3);
                    break;
                }
                case SensorType.Load when CoreLoad.Match(r.Name) is { Success: true } lm:
                {
                    int n = int.Parse(lm.Groups[1].Value), t = lm.Groups[2].Success ? int.Parse(lm.Groups[2].Value) : 1;
                    Get(n).Item3[t] = r.RawValue is float f ? $"{f:0}%" : Dash;
                    break;
                }
            }
        }
        return cores.Select(c => new CpuCoreRow($"Núcleo {c.Key}", c.Value.Clock, c.Value.Temp,
                                                c.Value.Loads.Count == 0 ? Dash
                                                : c.Value.Loads.Count == 1 ? c.Value.Loads.First().Value
                                                : string.Join(" · ", c.Value.Loads.Select(l => $"T{l.Key} {l.Value}")))).ToList();
    }
}
