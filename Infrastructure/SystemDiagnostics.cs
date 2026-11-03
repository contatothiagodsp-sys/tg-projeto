using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ThgOptimization.Infrastructure;

public enum MemoryPressureLevel { Low, Moderate, High }

/// <summary>Compressão de memória e Page Combining como o Windows informa. Null = não deu para ler.</summary>
public sealed record MmAgentState(bool? MemoryCompression, bool? PageCombining);

public sealed record MemoryReport(long TotalMb, long AvailableMb, double CommitPercent, MemoryPressureLevel Level);

/// <summary>
/// Pressão de memória, só leitura. Compressão e Page Combining são ajustes globais que reduzem o uso de RAM física, e nada comprova que
/// causem DPC alto: o app mostra o estado e não os altera. Só existe o comando de leitura aqui.
/// </summary>
public static class MemoryDiagnostics
{
    public const string ReadMmAgentCommand = "Get-MMAgent | Select-Object MemoryCompression,PageCombining | ConvertTo-Json -Compress";
    public const double ModerateAvailablePercent = 20, HighAvailablePercent = 10, HighCommitPercent = 90;

    public static MemoryPressureLevel Classify(double availablePercent, double commitPercent) =>
        availablePercent < HighAvailablePercent || commitPercent >= HighCommitPercent ? MemoryPressureLevel.High
        : availablePercent < ModerateAvailablePercent ? MemoryPressureLevel.Moderate
        : MemoryPressureLevel.Low;

    public static string LevelText(MemoryPressureLevel level) => level switch
    {
        MemoryPressureLevel.High => "alta",
        MemoryPressureLevel.Moderate => "moderada",
        _ => "baixa",
    };

    public static MemoryReport? Read()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m) || m.TotalPhys == 0) return null;
        double available = m.AvailPhys * 100.0 / m.TotalPhys;
        double commit = m.TotalPageFile > 0 ? (m.TotalPageFile - m.AvailPageFile) * 100.0 / m.TotalPageFile : 0;
        return new MemoryReport((long)(m.TotalPhys >> 20), (long)(m.AvailPhys >> 20), commit, Classify(available, commit));
    }

    /// <summary>Lê o JSON de Get-MMAgent. Texto vazio ou inválido vira null: indisponível, sem chutar.</summary>
    public static MmAgentState? ParseMmAgent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new MmAgentState(Flag(root, "MemoryCompression"), Flag(root, "PageCombining"));
        }
        catch (JsonException) { return null; }
    }

    private static bool? Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}

/// <summary>Tempos acumulados de um processador, em unidades de 100 ns.</summary>
public readonly record struct ProcessorTimes(long Dpc, long Interrupt, uint InterruptCount);

public readonly record struct CpuInterruptLoad(int Cpu, double DpcPercent, double IsrPercent, double InterruptsPerSecond);

/// <summary>
/// Quanto cada CPU gasta em DPC e ISR, por contadores do kernel. É só observação, e barata: duas leituras, sem ETW. Mostra onde a interrupção
/// cai, não qual driver a causou. Afinidade de interrupção não é alterada: sem conhecer o driver, seria só especulação.
/// </summary>
public static class InterruptDiagnostics
{
    private const int SystemProcessorPerformanceInformation = 8, EntrySize = 48;

    /// <summary>Cada entrada: Idle, Kernel e User (8 bytes cada), Dpc@24, Interrupt@32 e InterruptCount@40. Só o grupo de processadores atual.</summary>
    public static ProcessorTimes[] Parse(ReadOnlySpan<byte> buffer)
    {
        var list = new ProcessorTimes[buffer.Length / EntrySize];
        for (int i = 0; i < list.Length; i++)
        {
            var e = buffer.Slice(i * EntrySize, EntrySize);
            list[i] = new ProcessorTimes(BitConverter.ToInt64(e[24..]), BitConverter.ToInt64(e[32..]), BitConverter.ToUInt32(e[40..]));
        }
        return list;
    }

    public static unsafe ProcessorTimes[]? Read()
    {
        int size = Math.Min(Environment.ProcessorCount, 64) * EntrySize;
        var buffer = new byte[size];
        uint returned;
        fixed (byte* p = buffer)
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, p, (uint)size, &returned) != 0) return null;
        return Parse(buffer.AsSpan(0, (int)returned));
    }

    public static List<CpuInterruptLoad> Compute(IReadOnlyList<ProcessorTimes> before, IReadOnlyList<ProcessorTimes> after, double seconds)
    {
        var list = new List<CpuInterruptLoad>(Math.Min(before.Count, after.Count));
        if (seconds <= 0) return list;
        double ticks = seconds * 10_000_000;
        for (int i = 0; i < before.Count && i < after.Count; i++)
            list.Add(new CpuInterruptLoad(i, (after[i].Dpc - before[i].Dpc) * 100 / ticks, (after[i].Interrupt - before[i].Interrupt) * 100 / ticks,
                                          unchecked(after[i].InterruptCount - before[i].InterruptCount) / seconds));
        return list;
    }

    public static async Task<List<CpuInterruptLoad>?> SampleAsync(TimeSpan duration, CancellationToken cancel = default)
    {
        var before = Read();
        if (before == null) return null;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        await Task.Delay(duration, cancel).ConfigureAwait(false);
        var after = Read();
        return after == null ? null : Compute(before, after, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds);
    }

    public static string Summary(IReadOnlyList<CpuInterruptLoad> loads)
    {
        if (loads.Count == 0) return "sem dados";
        var busiest = loads[0];
        foreach (var l in loads) if (l.DpcPercent + l.IsrPercent > busiest.DpcPercent + busiest.IsrPercent) busiest = l;
        double dpc = 0, isr = 0;
        foreach (var l in loads) { dpc += l.DpcPercent; isr += l.IsrPercent; }
        return $"média DPC {dpc / loads.Count:0.00}% e ISR {isr / loads.Count:0.00}% por CPU; mais ocupada: CPU {busiest.Cpu} (DPC {busiest.DpcPercent:0.00}%, ISR {busiest.IsrPercent:0.00}%, {busiest.InterruptsPerSecond:0} interrupções/s)";
    }

    [DllImport("ntdll.dll")] private static extern unsafe int NtQuerySystemInformation(int infoClass, void* info, uint length, uint* returned);
}

public sealed record NetAdapterSettings(string Name, string Description, bool Connected, bool Virtual, IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>Valor mostrado pelo driver, ou aviso de que este adaptador não expõe a propriedade.</summary>
    public string Value(string keyword) => Properties.TryGetValue(keyword, out var v) ? v : "não informado";
}

public readonly record struct LinkQuality(int Sent, int Received, double AvgMs, double JitterMs, double LossPercent);

/// <summary>
/// Rede só leitura: nenhum valor de adaptador é alterado aqui. Interrupt Moderation desligado pode aumentar o uso de CPU e não ajuda em toda placa,
/// por isso o app mostra o estado e não recomenda mudar por padrão.
/// </summary>
public static class NetworkDiagnostics
{
    public static readonly (string Keyword, string Label)[] Keywords =
    {
        ("*InterruptModeration", "Interrupt Moderation"), ("*RSS", "RSS"), ("*RscIPv4", "RSC IPv4"), ("*RscIPv6", "RSC IPv6"),
        ("*ReceiveBuffers", "Buffers de recepção"), ("*TransmitBuffers", "Buffers de envio"),
    };

    /// <summary>Jitter = média da diferença entre respostas seguidas. Sem resposta conta como perda e fica fora da média.</summary>
    public static LinkQuality Summarize(IReadOnlyList<long?> roundTrips)
    {
        double sum = 0, deltas = 0;
        int received = 0, pairs = 0;
        long? previous = null;
        foreach (var rtt in roundTrips)
        {
            if (rtt is not long ms) continue;
            sum += ms;
            received++;
            if (previous is long p) { deltas += Math.Abs(ms - p); pairs++; }
            previous = ms;
        }
        return new LinkQuality(roundTrips.Count, received, received > 0 ? sum / received : 0, pairs > 0 ? deltas / pairs : 0,
                               roundTrips.Count > 0 ? (roundTrips.Count - received) * 100.0 / roundTrips.Count : 0);
    }

    public static List<NetAdapterSettings> ReadAdapters()
    {
        var byName = new Dictionary<string, (string Description, bool Connected, bool Virtual, Dictionary<string, string> Properties)>(StringComparer.OrdinalIgnoreCase);
        var scope = new ManagementScope(@"\\.\root\StandardCimv2");
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, InterfaceDescription, MediaConnectState, Virtual FROM MSFT_NetAdapter")))
        using (var all = s.Get())
            foreach (ManagementObject o in all)
                using (o)
                    if (o["Name"]?.ToString() is string name)
                        byName[name] = (o["InterfaceDescription"]?.ToString() ?? "", Convert.ToUInt32(o["MediaConnectState"] ?? 0u) == 1, o["Virtual"] is true, new(StringComparer.OrdinalIgnoreCase));
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, RegistryKeyword, DisplayValue FROM MSFT_NetAdapterAdvancedPropertySettingData")))
        using (var all = s.Get())
            foreach (ManagementObject o in all)
                using (o)
                    if (o["Name"]?.ToString() is string name && byName.TryGetValue(name, out var adapter) && o["RegistryKeyword"]?.ToString() is string key)
                        adapter.Properties[key] = o["DisplayValue"]?.ToString() ?? "";
        return byName.Select(kv => new NetAdapterSettings(kv.Key, kv.Value.Description, kv.Value.Connected, kv.Value.Virtual, kv.Value.Properties)).ToList();
    }

    /// <summary>Gateway IPv4 do primeiro adaptador conectado. É o trecho até o roteador: separa problema local de problema do provedor.</summary>
    public static System.Net.IPAddress? DefaultGateway()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var g in nic.GetIPProperties().GatewayAddresses)
                if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)) return g.Address;
        }
        return null;
    }

    public static async Task<LinkQuality?> ProbeGatewayAsync(int count = 20, CancellationToken cancel = default)
    {
        if (DefaultGateway() is not { } gateway) return null;
        var samples = new List<long?>(count);
        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(gateway, 1000).ConfigureAwait(false);
                samples.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
            catch (PingException) { samples.Add(null); }
            await Task.Delay(200, cancel).ConfigureAwait(false);
        }
        return Summarize(samples);
    }
}
