using System.IO.Ports;
using System.IO;

namespace ThgOptimization;

/// <summary>
/// Telas Turing Smart Screen / TURZX que aparecem como porta COM, no protocolo "revisão A" (3,5" CH552/CH340):
/// comando de 6 bytes com as coordenadas em 10 bits e o código no último byte (197 = imagem, 110 = brilho,
/// 101 = reiniciar), seguido da imagem em RGB565 little-endian, em blocos de largura×8 bytes.
/// Nas telas TURZX novas (1A86:CA21 "CT21INCH") o protocolo não é documentado: o tHg oferece como TESTE.
/// Mandar comandos que a tela não entende não a danifica; no pior caso ela ignora ou é preciso desconectar e conectar.
/// </summary>
public sealed class TuringSerial : IDisposable
{
    private const byte CmdReset = 101, CmdBrightness = 110, CmdBitmap = 197;
    private readonly SerialPort _port;

    private TuringSerial(SerialPort p) => _port = p;

    public static TuringSerial Open(string com)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(com, @"^COM\d{1,3}$")) throw new ArgumentException("Porta inválida.");
        var p = new SerialPort(com, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.RequestToSend, RtsEnable = true, DtrEnable = true, ReadTimeout = 2000, WriteTimeout = 2000,
        };
        p.Open();
        return new TuringSerial(p);
    }

    private void Command(byte cmd, int x, int y, int ex, int ey)
    {
        var b = new byte[6];
        b[0] = (byte)(x >> 2);
        b[1] = (byte)(((x & 3) << 6) + (y >> 4));
        b[2] = (byte)(((y & 15) << 4) + (ex >> 6));
        b[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
        b[4] = (byte)(ey & 255);
        b[5] = cmd;
        _port.Write(b, 0, 6);
    }

    /// <summary>Brilho 0–100 (na tela, 0 = mais claro e 255 = apagado).</summary>
    public void SetBrightness(int percent) => Command(CmdBrightness, 255 - (int)(Math.Clamp(percent, 0, 100) * 2.55), 0, 0, 0);

    /// <summary>Imagem inteira (RGB565 LE, largura×altura×2 bytes).</summary>
    public void SendImage(byte[] rgb565, int width, int height)
    {
        if (rgb565.Length != width * height * 2) throw new ArgumentException("Tamanho da imagem não confere com a tela.");
        Command(CmdBitmap, 0, 0, width - 1, height - 1);
        int chunk = width * 8;
        for (int off = 0; off < rgb565.Length; off += chunk)
            _port.Write(rgb565, off, Math.Min(chunk, rgb565.Length - off));
    }

    public void Dispose() { try { _port.Close(); } catch { } _port.Dispose(); }
}

/// <summary>
/// Telas TURZX / Turing "revisão C": 2,1" e 2,8" redondas (480×480), 5" e 8,8". Mesmo protocolo documentado pelo projeto
/// turing-smart-screen-python (reimplementado aqui): mensagens em blocos de 250 bytes, imagem em BGRA (4 bytes por pixel)
/// com um 0x00 a cada 249 bytes, e confirmação de 1024 bytes depois de cada quadro.
/// A tela "dorme" aparecendo como 1A86:CA21 (CT21INCH); abrir a porta a acorda e ela volta como 1D6B:0121 (ou 1D6B:0106 /
/// 0525:A4A7), que é a porta usada para desenhar.
/// </summary>
public sealed class TuringRevC : IDisposable
{
    private static readonly byte[] Hello = { 0x01, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xC5, 0xD3 };
    private static readonly byte[] Options = { 0x7D, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x2D };
    private static readonly byte[] SetBrightnessCmd = { 0x7B, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
    private static readonly byte[] StopVideo = { 0x79, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 };
    private static readonly byte[] StopMedia = { 0x96, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 };
    private static readonly byte[] QueryStatus = { 0xCF, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 };
    private static readonly byte[] PreUpdate = { 0x86, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01 };
    private static readonly byte[] DisplayBitmap2Inch = { 0xC8, 0xEF, 0x69, 0x00, 0x0E, 0x10 };

    private readonly SerialPort _port;
    public string Port { get; }
    public string Id { get; private set; } = "";

    private TuringRevC(SerialPort p, string port) { _port = p; Port = port; }

    /// <summary>Porta da tela acordada, ou null.</summary>
    public static string? FindAwakePort() => ComFor(("1D6B", "0121"), ("1D6B", "0106"), ("0525", "A4A7"));

    public static string? FindSleepingPort() => ComFor(("1A86", "CA21"));

    private static string? ComFor(params (string Vid, string Pid)[] ids)
    {
        try
        {
            foreach (var d in UsbInventory.Scan())
                if (d.ComPort != null && ids.Any(i => i.Vid == d.Vid && i.Pid == d.Pid)) return d.ComPort;
        }
        catch { }
        return null;
    }

    /// <summary>Acorda a tela (abrindo a porta "dormindo" a cada segundo, até 15 s) e devolve a porta acordada.</summary>
    public static string? WakeAndFind(Action<string>? status = null)
    {
        if (FindAwakePort() is string awake) return awake;
        string? sleeping = FindSleepingPort();
        if (sleeping == null) return null;
        for (int i = 0; i < 15; i++)
        {
            status?.Invoke($"Acordando a tela TURZX ({i + 1}/15)…");
            try { using var p = NewPort(sleeping); p.Open(); Thread.Sleep(200); } catch { }
            Thread.Sleep(1000);
            if (FindAwakePort() is string now) { Thread.Sleep(1000); return now; }
        }
        return null;
    }

    private static SerialPort NewPort(string com) => new(com, 115200, Parity.None, 8, StopBits.One)
    {
        Handshake = Handshake.RequestToSend, ReadTimeout = 1000, WriteTimeout = 3000,
    };

    public static TuringRevC Open(string com, int brightness)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(com, @"^COM\d{1,3}$")) throw new ArgumentException("Porta inválida.");
        var p = NewPort(com);
        p.Open();
        var t = new TuringRevC(p, com);
        try { t.Init(brightness); } catch { t.Dispose(); throw; }
        return t;
    }

    private void Send(byte[] cmd, byte[]? payload = null, byte pad = 0x00)
    {
        int len = cmd.Length + (payload?.Length ?? 0);
        int total = (len + 249) / 250 * 250;
        var msg = new byte[total];
        cmd.CopyTo(msg, 0);
        payload?.CopyTo(msg, cmd.Length);
        if (pad != 0) for (int i = len; i < total; i++) msg[i] = pad;
        _port.Write(msg, 0, msg.Length);
    }

    private byte[] Read(int n)
    {
        var buf = new byte[n];
        int got = 0;
        try { while (got < n) { int r = _port.Read(buf, got, n - got); if (r <= 0) break; got += r; } }
        catch (TimeoutException) { }
        return buf[..got];
    }

    private void Init(int brightness)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            _port.DiscardInBuffer();
            Send(Hello);
            var r = Read(23);
            Id = new string(System.Text.Encoding.ASCII.GetString(r).Where(c => c >= ' ' && c < 127).ToArray());
            _port.DiscardInBuffer();
            if (Id.StartsWith("chs_", StringComparison.Ordinal)) break;
            if (attempt == 4) throw new IOException($"A tela não se identificou (respondeu \"{Id}\").");
            Thread.Sleep(1000);
        }
        Send(StopVideo);
        Send(StopMedia); Read(1024);
        SetBrightness(brightness);
        Send(Options, new byte[] { 0x00, 0x00, 0x00, 0x00 }); // início padrão, sem inverter, sem desligar sozinha
    }

    /// <summary>Brilho 0–100 (255 = mais claro).</summary>
    public void SetBrightness(int percent) => Send(SetBrightnessCmd, new[] { (byte)(Math.Clamp(percent, 0, 100) * 255 / 100) });

    /// <summary>Quadro inteiro 480×480 em BGRA (4 bytes por pixel).</summary>
    public void SendFrame480(byte[] bgra)
    {
        if (bgra.Length != 480 * 480 * 4) throw new ArgumentException("A imagem precisa ter 480×480.");
        // Um 0x00 depois de cada 249 bytes de imagem (o protocolo separa blocos de 250).
        int chunks = (bgra.Length + 248) / 249;
        var data = new byte[bgra.Length + chunks - 1];
        for (int i = 0, o = 0; i < bgra.Length; i += 249)
        {
            int n = Math.Min(249, bgra.Length - i);
            Buffer.BlockCopy(bgra, i, data, o, n);
            o += n;
            if (i + n < bgra.Length) data[o++] = 0x00;
        }
        Send(PreUpdate);
        Send(new byte[] { 0x2C }, null, 0x2C);
        Send(DisplayBitmap2Inch, new byte[] { 0x0E, 0x10 }); // 480×480/64 = 3600
        _port.Write(data, 0, data.Length);
        int rest = (250 - data.Length % 250) % 250;
        if (rest > 0) _port.Write(new byte[rest], 0, rest);
        Read(1024);
        Send(QueryStatus); Read(1024);
    }

    public void Dispose() { try { _port.Close(); } catch { } _port.Dispose(); }
}
