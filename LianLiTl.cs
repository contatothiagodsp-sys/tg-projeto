using System.IO;

namespace ThgOptimization;

/// <summary>
/// Lian Li UNI FAN TL: controladora das fans (0416:7372, HID relatório 0x01 de 64 bytes, página de uso 0xFF1B) e as
/// telas das fans TL LCD (04FC:7393, HID relatório 0x02 de 512 bytes, JPEG 400×400).
/// Protocolo conforme o projeto lian-li-linux (licença MIT, © 2026 sgtaziz), reimplementado em C#.
/// O L-Connect 3 precisa estar fechado (ou pausado): dois programas falando com o mesmo aparelho se atrapalham.
/// </summary>
public sealed class TlFanHub : IDisposable
{
    private const byte ReportId = 0x01, CmdHandshake = 0xA1, CmdSetSpeed = 0xAA, CmdMbSync = 0xB1;
    private readonly HidDevice _dev;
    private readonly object _io = new();
    /// <summary>Fans detectadas em cada uma das 4 portas.</summary>
    public int[] FansPerPort { get; } = new int[4];
    private readonly int[] _rpm = new int[4];
    private DateTime _lastHandshake;
    private readonly bool[] _syncOff = new bool[4];
    private readonly int[] _last = { -1, -1, -1, -1 };

    private TlFanHub(HidDevice dev) => _dev = dev;

    public static TlFanHub? Open()
    {
        foreach (var path in HidDevice.Find(0x0416, 0x7372))
        {
            var d = HidDevice.Open(path);
            if (d == null) continue;
            if (d.UsagePage == 0xFF1B && d.OutputLength >= 64) { var hub = new TlFanHub(d); hub.Handshake(); return hub; }
            d.Dispose();
        }
        return null;
    }

    private byte[]? Command(byte cmd, ReadOnlySpan<byte> data, int timeoutMs = 300)
    {
        var pkt = new byte[64];
        pkt[0] = ReportId; pkt[1] = cmd; pkt[5] = (byte)Math.Min(data.Length, 58);
        data[..Math.Min(data.Length, 58)].CopyTo(pkt.AsSpan(6));
        lock (_io)
        {
            _dev.Write(pkt);
            for (int i = 0; i < 5; i++)
            {
                var r = _dev.Read(timeoutMs);
                if (r == null) return null;
                if (r.Length > 1 && r[1] == cmd) return r;
            }
        }
        return null;
    }

    /// <summary>Lê quantas fans há em cada porta e a rotação (resposta: 3 bytes por fan).</summary>
    public void Handshake()
    {
        var r = Command(CmdHandshake, ReadOnlySpan<byte>.Empty, 1500);
        _lastHandshake = DateTime.Now;
        if (r == null || r.Length < 7) return;
        Array.Clear(FansPerPort); Array.Clear(_rpm);
        int len = Math.Min(r[5], r.Length - 6);
        for (int o = 0; o + 2 < len; o += 3)
        {
            byte info = r[6 + o];
            if ((info & 0x80) == 0) continue;
            int port = (info >> 4) & 3, idx = info & 0x0F;
            FansPerPort[port] = Math.Max(FansPerPort[port], idx + 1);
            if (idx == 0) _rpm[port] = (r[7 + o] << 8) | r[8 + o];
        }
    }

    public string RpmText(int port)
    {
        if ((DateTime.Now - _lastHandshake).TotalSeconds > 3) try { Handshake(); } catch { }
        return _rpm[port] > 0 ? $"{_rpm[port]} RPM" : "sem leitura de RPM";
    }

    /// <summary>Velocidade (0–100%) de todas as fans da porta. Desliga a sincronia com a placa-mãe dessa porta.</summary>
    public void SetPort(int port, float percent)
    {
        byte pct = (byte)Math.Clamp((int)Math.Round(percent), 0, 100);
        if (pct == _last[port]) return; // só manda quando muda
        for (int i = 0; i < FansPerPort[port]; i++)
        {
            byte addr = (byte)((port << 4) | i);
            if (!_syncOff[port]) Command(CmdMbSync, new[] { addr }, 150); // bit 7 = 0: sem sincronia com a placa-mãe
            Command(CmdSetSpeed, new[] { addr, pct }, 150);
        }
        _syncOff[port] = true; _last[port] = pct;
    }

    /// <summary>Saídas para a página Fans. "Padrão" = 60% (o hub não tem uma curva própria para voltar).</summary>
    public IEnumerable<FanOutput> Outputs() =>
        Enumerable.Range(0, 4).Where(p => FansPerPort[p] > 0).Select(p => new FanOutput(
            $"lianli-tl-port{p}", "Lian Li UNI FAN TL", $"Porta {p + 1} ({FansPerPort[p]} fan{(FansPerPort[p] > 1 ? "s" : "")})",
            v => SetPort(p, v), () => SetPort(p, 60), () => RpmText(p)));

    public void Dispose() => _dev.Dispose();
}

/// <summary>
/// Uma tela de fan UNI FAN TL LCD (400×400). Cada fan é um aparelho USB próprio (04FC:7393), então cada uma recebe a sua
/// imagem. A posição física vem da própria fan (comando 62: porta da controladora e posição na corrente), o que dá um
/// nome estável ("porta 2, fan 1") mesmo se o Windows mudar a ordem dos aparelhos.
/// </summary>
public sealed class TlLcdScreen : IDisposable
{
    private const byte ReportId = 0x02, CmdReadSerial = 62, CmdWriteJpg = 0x41, CmdSyncJpg = 0x46, CmdLcdControl = 64;
    private const int Payload = 501;
    public const int MaxJpeg = 65_535;
    private readonly HidDevice _dev;
    private readonly object _io = new();

    public int Port { get; private set; } = -1;
    public int Index { get; private set; } = -1;
    public string DevicePath => _dev.Path;

    /// <summary>Chave estável da tela: posição física se a fan informou, senão o caminho do aparelho.</summary>
    public string Key => Port >= 0 ? $"tl:{Port}:{Index}" : "tl:" + (uint)DevicePath.ToLowerInvariant().GetHashCode();
    public string Label => Port >= 0 ? $"Fan {Index + 1} · porta {Port + 1}" : "Fan TL LCD";

    private TlLcdScreen(HidDevice d) => _dev = d;

    /// <summary>Todas as telas TL LCD conectadas, com a posição lida de cada uma.</summary>
    public static List<TlLcdScreen> OpenAll()
    {
        var list = new List<TlLcdScreen>();
        foreach (var path in HidDevice.Find(0x04FC, 0x7393))
        {
            var d = HidDevice.Open(path);
            if (d == null) continue;
            if (d.OutputLength < 512) { d.Dispose(); continue; }
            var s = new TlLcdScreen(d);
            try { s.ReadIdentity(); } catch { }
            list.Add(s);
        }
        // Mesma posição informada por duas fans (firmware sem posição): usa o caminho para separar.
        foreach (var dup in list.GroupBy(x => x.Key).Where(g => g.Count() > 1).SelectMany(g => g)) dup.Port = dup.Index = -1;
        return list.OrderBy(x => x.Port < 0 ? 99 : x.Port).ThenBy(x => x.Index).ToList();
    }

    private void ReadIdentity()
    {
        lock (_io)
        {
            _dev.Write(Packet(CmdReadSerial, 0, 0, ReadOnlySpan<byte>.Empty));
            var r = _dev.Read(1500);
            if (r == null || r.Length < 11 + 34 || r[1] != CmdReadSerial) return;
            Port = r[11 + 32] & 0x03;
            Index = r[11 + 33];
        }
    }

    // Um pacote de 512 bytes reaproveitado (sempre dentro do lock _io): um quadro de 400×400 são ~100 pacotes por fan por segundo.
    private readonly byte[] _pkt = new byte[512];

    private byte[] Packet(byte cmd, int total, int number, ReadOnlySpan<byte> chunk)
    {
        var p = _pkt;
        Array.Clear(p);
        p[0] = ReportId; p[1] = cmd;
        p[2] = (byte)(total >> 24); p[3] = (byte)(total >> 16); p[4] = (byte)(total >> 8); p[5] = (byte)total;
        p[6] = (byte)(number >> 16); p[7] = (byte)(number >> 8); p[8] = (byte)number;
        int len = Math.Min(chunk.Length, Payload);
        p[9] = (byte)(len >> 8); p[10] = (byte)len;
        chunk[..len].CopyTo(p.AsSpan(11));
        return p;
    }

    /// <summary>Imagem fixa: cada pacote confirmado, depois a tela muda para "mostrar imagem" com o brilho pedido.</summary>
    public void SendStatic(ReadOnlySpan<byte> jpeg, int brightness)
    {
        if (jpeg.Length == 0 || jpeg.Length > MaxJpeg) throw new ArgumentException($"Imagem com {jpeg.Length:N0} bytes: o limite da tela é 64 KB.");
        lock (_io)
        {
            for (int off = 0, n = 0; off < jpeg.Length; off += Payload, n++)
            {
                _dev.Write(Packet(CmdWriteJpg, jpeg.Length, n, jpeg.Slice(off, Math.Min(Payload, jpeg.Length - off))));
                var ack = _dev.Read(500);
                if (ack == null || ack.Length < 2 || ack[1] != CmdWriteJpg) throw new IOException("a tela não confirmou o pacote (o L-Connect 3 está aberto?)");
            }
            var payload = new byte[11];
            payload[0] = 1; payload[4] = (byte)Math.Clamp(brightness, 0, 100); payload[5] = 30;
            _dev.Write(Packet(CmdLcdControl, payload.Length, 0, payload));
            _dev.Read(300);
        }
    }

    /// <summary>Quadro contínuo (sensores, GIF): sem esperar confirmação, como o modo de streaming do L-Connect.</summary>
    public void SendFrame(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length == 0 || jpeg.Length > MaxJpeg) return;
        lock (_io)
            for (int off = 0, n = 0; off < jpeg.Length; off += Payload, n++)
                _dev.Write(Packet(CmdSyncJpg, jpeg.Length, n, jpeg.Slice(off, Math.Min(Payload, jpeg.Length - off))), 1000);
    }

    public void Dispose() => _dev.Dispose();
}
