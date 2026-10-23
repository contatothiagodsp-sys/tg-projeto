using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace ThgOptimization;

public sealed record UsbDev(string Vid, string Pid, string Interface, string Name, string Class, string Service, string Status,
                            string Manufacturer, string? ComPort, string InstanceId, KnownUsb? Known);

/// <summary>Aparelho reconhecido: o que é, a resolução da tela (se tiver) e o estado do suporte no tHg.</summary>
public sealed record KnownUsb(string Brand, string Model, int Width, int Height, bool Round, string Transport, string Support);

/// <summary>
/// Diagnóstico USB: lista os dispositivos USB e HID com VID/PID, interface, driver e porta COM, e reconhece telas e
/// controladoras de fans/RGB. A tabela da Lian Li segue o projeto lian-li-linux (licença MIT, © 2026 sgtaziz); a da
/// Turing/Turzx, o turing-smart-screen-python e relatos de usuários. O relatório salvo serve para identificar o modelo
/// exato e o protocolo antes de qualquer envio à tela.
/// </summary>
public static class UsbInventory
{
    private static readonly Dictionary<(string, string), KnownUsb> Known = Build();

    private static Dictionary<(string, string), KnownUsb> Build()
    {
        var d = new Dictionary<(string, string), KnownUsb>();
        void K(string vid, string pid, string brand, string model, int w = 0, int h = 0, bool round = false, string transport = "HID", string support = "identificado")
            => d[(vid, pid)] = new KnownUsb(brand, model, w, h, round, transport, support);

        // Turing / TURZX (telas de gabinete e de cooler)
        K("1A86", "5722", "Turing/TURZX", "Smart Screen 3,5\"/5\"/8,8\" (CH552, rev. B/C)", 320, 480, transport: "Serial (COM)", support: "protocolo público (serial)");
        K("1A86", "7523", "Turing/TURZX ou adaptador CH340", "Smart Screen 3,5\" rev. A ou conversor USB-serial", 320, 480, transport: "Serial (COM)", support: "confirmar modelo");
        K("1CBE", "0028", "Turing/TURZX", "Tela redonda 2,8\" (V1.x)", 480, 480, true, "USB bulk", "protocolo USB próprio");
        K("1A86", "CA21", "TURZX", "Tela redonda 2,1\"/2,8\" (CT21INCH, dormindo)", 480, 480, true, "Serial (COM)", "revisão C: o tHg acorda e desenha");
        K("1D6B", "0121", "TURZX/Turing", "Tela revisão C (acordada)", 480, 480, true, "Serial (COM)", "revisão C");
        K("1D6B", "0106", "TURZX/Turing", "Tela revisão C (acordada)", 480, 480, true, "Serial (COM)", "revisão C");
        K("0525", "A4A7", "TURZX/Turing", "Tela revisão C (acordada)", 480, 480, true, "Serial (COM)", "revisão C");
        for (int p = 0xAD10; p <= 0xAD3F; p++)
            if (!d.ContainsKey(("1A86", p.ToString("X4"))))
                K("1A86", p.ToString("X4"), "TURZX", "Tela USB (modo área de trabalho)", 0, 0, false, "USB (MJPEG)", "protocolo TURZX (MJPEG)");

        // Lian Li (fonte: lian-li-linux, MIT)
        K("39B0", "0101", "Lian Li", "Edge Hub Advanced");
        K("0416", "8040", "Lian Li", "Dongle sem fio TX"); K("0416", "8041", "Lian Li", "Dongle sem fio RX");
        K("1A86", "E304", "Lian Li", "Dongle sem fio V2 TX"); K("1A86", "E305", "Lian Li", "Dongle sem fio V2 RX");
        K("1A86", "2107", "Lian Li", "Dongle V2 (interface HID)");
        K("0416", "8050", "Lian Li", "Universal Screen 8,8\" (anel de LED)");
        for (int p = 0xA100; p <= 0xA106; p++) K("0CF2", p.ToString("X4"), "Lian Li", "Controladora UNI FAN SL / AL / SL-INF (ENE 6K77)");
        K("0CF2", "A200", "Lian Li", "Strimer Plus");
        K("0416", "7372", "Lian Li", "Controladora UNI FAN TL");
        K("04FC", "7393", "Lian Li", "UNI FAN TL LCD", 400, 400, transport: "HID");
        K("0416", "7371", "Lian Li", "Galahad II Trinity AIO"); K("0416", "7373", "Lian Li", "Galahad II Trinity AIO");
        foreach (var p in new[] { "7398", "7399", "739A" }) K("0416", p, "Lian Li", "HydroShift LCD AIO", 480, 480, transport: "HID");
        K("0416", "7391", "Lian Li", "Galahad II LCD AIO", 480, 480, transport: "HID");
        K("0416", "7395", "Lian Li", "Galahad II Vision AIO", 480, 480, transport: "HID");
        K("1CBE", "0005", "Lian Li", "UNI FAN SL V3 LCD (sem fio)", 400, 400, transport: "USB bulk (criptografado)");
        K("1CBE", "0006", "Lian Li", "UNI FAN TL V2 LCD (sem fio)", 400, 400, transport: "USB bulk (criptografado)");
        K("1CBE", "A021", "Lian Li", "HydroShift II LCD Circle", 480, 480, true, "USB bulk (criptografado)");
        K("1CBE", "A034", "Lian Li", "HydroShift II LCD Square", 480, 480, false, "USB bulk (criptografado)");
        K("1CBE", "A065", "Lian Li", "Lancool 207 Digital", 720, 1472, false, "USB bulk (criptografado)");
        K("1CBE", "A088", "Lian Li", "Universal Screen 8,8\"", 480, 1920, false, "USB bulk (criptografado)");
        K("1CBE", "A092", "Lian Li", "Vision 9,2\"", 464, 1920, false, "USB bulk (criptografado)");
        K("1CBE", "A018", "Lian Li", "TL Flex LCD", 400, 400, false, "USB bulk (criptografado)");
        K("1CBE", "A019", "Lian Li", "SL Infinity Flex LCD", 400, 400, false, "USB bulk (criptografado)");
        K("1CBE", "A068", "Lian Li", "HydroShift II OLED Curve", 1080, 2288, false, "USB bulk (criptografado)");
        K("1A86", "AD20", "Lian Li", "HydroShift II LCD Circle (modo área de trabalho)", 480, 480, true, "USB (MJPEG)");
        K("1A86", "AD22", "Lian Li", "HydroShift II LCD Square (modo área de trabalho)", 480, 480, false, "USB (MJPEG)");
        K("1A86", "AD23", "Lian Li", "HydroShift II OLED Curve (modo área de trabalho)", 1080, 2288, false, "USB (MJPEG)");
        K("1A86", "ACD1", "Lian Li", "Lancool 207 Digital (modo área de trabalho)", 720, 1472, false, "USB (MJPEG)");
        K("1A86", "AD11", "Lian Li", "Lancool 207 Digital (modo área de trabalho)", 720, 1472, false, "USB (MJPEG)");
        K("1A86", "ACE1", "Lian Li", "Universal Screen 8,8\" (modo área de trabalho)", 480, 1920, false, "USB (MJPEG)");
        K("1A86", "AD21", "Lian Li", "Universal Screen 8,8\" (modo área de trabalho)", 480, 1920, false, "USB (MJPEG)");
        K("1A86", "AD26", "Lian Li", "Vision 9,2\" (modo área de trabalho)", 464, 1920, false, "USB (MJPEG)");
        foreach (var (p, n) in new[] { ("0101", "TL Flex"), ("0102", "TL Flex LCD"), ("0103", "SL-INF Flex"), ("0104", "SL-INF Flex LCD"),
                                       ("0105", "P28 V2"), ("0106", "SL V4"), ("0107", "CL V2") })
            K("43A8", p, "Lian Li", $"Controladora {n}");
        return d;
    }

    public static List<UsbDev> Scan()
    {
        var list = new List<UsbDev>();
        using var s = new ManagementObjectSearcher(
            "SELECT DeviceID, Name, PNPClass, Service, Status, Manufacturer FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%' OR DeviceID LIKE 'HID\\\\VID_%'");
        foreach (ManagementObject o in s.Get())
        {
            string id = o["DeviceID"]?.ToString() ?? "";
            var m = Regex.Match(id, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&MI_([0-9A-F]{2}))?", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string vid = m.Groups[1].Value.ToUpperInvariant(), pid = m.Groups[2].Value.ToUpperInvariant();
            string name = o["Name"]?.ToString() ?? "";
            string? com = Regex.Match(name, @"\((COM\d+)\)") is { Success: true } c ? c.Groups[1].Value : null;
            Known.TryGetValue((vid, pid), out var k);
            list.Add(new UsbDev(vid, pid, m.Groups[3].Success ? m.Groups[3].Value : "", name, o["PNPClass"]?.ToString() ?? "",
                                o["Service"]?.ToString() ?? "", o["Status"]?.ToString() ?? "", o["Manufacturer"]?.ToString() ?? "", com, id, k));
        }
        return list.OrderBy(d => d.Known == null).ThenBy(d => d.Vid).ThenBy(d => d.Pid).ThenBy(d => d.Interface).ToList();
    }

    public static string Report(List<UsbDev> devs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"tHg Optimization {AppUpdater.Current} · diagnóstico USB · {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}");
        sb.AppendLine();
        var known = devs.Where(d => d.Known != null).GroupBy(d => (d.Vid, d.Pid)).ToList();
        sb.AppendLine(known.Count == 0 ? "Nenhuma tela ou controladora conhecida encontrada." : "RECONHECIDOS:");
        foreach (var g in known)
        {
            var k = g.First().Known!;
            sb.AppendLine($"  {g.Key.Vid}:{g.Key.Pid}  {k.Brand} · {k.Model}" + (k.Width > 0 ? $" · {k.Width}x{k.Height}{(k.Round ? " redonda" : "")}" : "") +
                          $" · {k.Transport} · {k.Support}");
            foreach (var d in g) sb.AppendLine($"      MI {(d.Interface.Length > 0 ? d.Interface : "--")} · {d.Class} · driver {d.Service} · {d.Status}" +
                                               (d.ComPort != null ? $" · {d.ComPort}" : "") + $" · {d.InstanceId}");
        }
        sb.AppendLine();
        sb.AppendLine("TODOS OS DISPOSITIVOS USB/HID:");
        foreach (var d in devs)
            sb.AppendLine($"  {d.Vid}:{d.Pid} MI {(d.Interface.Length > 0 ? d.Interface : "--")}  {d.Class,-16} {d.Service,-14} {d.Status,-6} {d.Name}" +
                          (d.ComPort != null ? $"  [{d.ComPort}]" : ""));
        return sb.ToString();
    }
}
