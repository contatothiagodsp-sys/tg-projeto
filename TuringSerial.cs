using System.IO.Ports;
using System.IO;

namespace ThgOptimization;

/// <summary>
/// Programas que costumam prender a porta COM de uma tela LCD. Lê a lista de processos do kernel, sem abrir nenhum deles
/// (seguro com anti-cheat). A única exceção é o "smartscreen": o nome é o mesmo do filtro do Windows, sempre aberto,
/// que nunca segura porta serial. Só para separar os dois, o caminho dele é consultado com o acesso mínimo.
/// </summary>
public static class TuringConflictDetector
{
    private static readonly string[] Known =
    {
        "TURZX", "TuringSmartScreen", "UsbMonitor", "SystemMonitor", "SmartScreen", "aida64", "HWiNFO32", "HWiNFO64",
        "iCUE", "Corsair.Service", "Razer Synapse", "RazerCentralService",
    };

    public static List<string> DetectRunningConflicts()
    {
        var running = Infrastructure.ProcessUtil.Snapshot();
        return Match(running, pid => IsWindowsImage(Infrastructure.ProcessUtil.ImagePath(pid)));
    }

    /// <summary>Nomes conhecidos entre os processos em execução, sem repetir. "Razer Synapse" também pega "Razer Synapse 3".</summary>
    internal static List<string> Match(IEnumerable<Infrastructure.ProcessSnapshot> running, Func<int, bool> isSystemProcess)
    {
        var found = new List<string>();
        foreach (var p in running)
        {
            string? known = Array.Find(Known, k => p.Name.Equals(k, StringComparison.OrdinalIgnoreCase)
                                                   || p.Name.StartsWith(k + " ", StringComparison.OrdinalIgnoreCase));
            if (known == null || found.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (known.Equals("SmartScreen", StringComparison.OrdinalIgnoreCase) && isSystemProcess(p.Pid)) continue;
            found.Add(p.Name);
        }
        return found;
    }

    /// <summary>Caminho desconhecido conta como do sistema: acusar o filtro do Windows pediria para fechar algo que não é o problema.</summary>
    internal static bool IsWindowsImage(string? path)
    {
        if (path == null) return true;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A porta COM da tela está aberta por outro programa. A mensagem diz o que fechar e o que clicar depois.</summary>
public sealed class LcdPortLockedException : UnauthorizedAccessException
{
    public string PortName { get; }
    public IReadOnlyList<string> ConflictingProcesses { get; }

    public LcdPortLockedException(string portName, IReadOnlyList<string> conflictingProcesses, Exception? inner = null)
        : base(BuildMessage(portName, conflictingProcesses), inner)
    {
        PortName = portName;
        ConflictingProcesses = conflictingProcesses;
    }

    private static string BuildMessage(string port, IReadOnlyList<string> processes) => processes.Count > 0
        ? $"A porta {port} está ocupada. Feche {string.Join(", ", processes)} e clique em RECONECTAR."
        : $"A porta {port} está ocupada por outro programa. Feche o software da tela (TURZX, AIDA64 ou parecido) e clique em RECONECTAR.";
}

/// <summary>Abertura de porta COM com novas tentativas: um programa fechando ou o usbser.sys soltando a porta leva alguns instantes.</summary>
internal static class LcdPortOpener
{
    private const int Attempts = 3;

    /// <summary>
    /// <paramref name="open"/> cria e abre a porta, e descarta o que criou se falhar. Só acesso negado e erro de E/S tentam de novo.
    /// Acesso negado até o fim vira <see cref="LcdPortLockedException"/>, com quem estiver prendendo a porta.
    /// </summary>
    internal static T Open<T>(string com, Func<T> open, Func<IReadOnlyList<string>>? conflicts = null, Action<int>? sleep = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return open(); }
            catch (UnauthorizedAccessException ex) when (attempt >= Attempts)
            {
                throw new LcdPortLockedException(com, (conflicts ?? TuringConflictDetector.DetectRunningConflicts)(), ex);
            }
            catch (Exception ex) when (attempt < Attempts && ex is UnauthorizedAccessException or IOException)
            {
                Infrastructure.AppLog.Debug("lcd", ex);
                (sleep ?? Thread.Sleep)(250 * attempt);
            }
        }
    }

    /// <summary>Executa a abertura na porta nova; em qualquer falha, descarta a porta para o handle nunca vazar.</summary>
    internal static T Using<T>(SerialPort candidate, Func<SerialPort, T> connect)
    {
        try { return connect(candidate); }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }
}

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
    private readonly object _gate = new();

    private TuringSerial(SerialPort p) => _port = p;

    public static TuringSerial Open(string com)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(com, @"^COM\d{1,3}$")) throw new ArgumentException("Porta inválida.");
        return LcdPortOpener.Open(com, () => LcdPortOpener.Using(new SerialPort(com, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.RequestToSend, RtsEnable = true, DtrEnable = true, ReadTimeout = 2000, WriteTimeout = 2000,
        }, candidate =>
        {
            candidate.Open();
            return new TuringSerial(candidate);
        }));
    }

    private readonly byte[] _cmd = new byte[6];

    private void Command(byte cmd, int x, int y, int ex, int ey)
    {
        var b = _cmd;
        b[0] = (byte)(x >> 2);
        b[1] = (byte)(((x & 3) << 6) + (y >> 4));
        b[2] = (byte)(((y & 15) << 4) + (ex >> 6));
        b[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
        b[4] = (byte)(ey & 255);
        b[5] = cmd;
        _port.Write(b, 0, 6);
    }

    /// <summary>Brilho 0–100 (na tela, 0 = mais claro e 255 = apagado).</summary>
    public void SetBrightness(int percent)
    {
        lock (_gate) Command(CmdBrightness, 255 - (int)(Math.Clamp(percent, 0, 100) * 2.55), 0, 0, 0);
    }

    /// <summary>Imagem inteira (RGB565 LE, largura×altura×2 bytes).</summary>
    public void SendImage(ArraySegment<byte> rgb565, int width, int height)
    {
        if (rgb565.Count != width * height * 2) throw new ArgumentException("Tamanho da imagem não confere com a tela.");
        lock (_gate)
        {
            Command(CmdBitmap, 0, 0, width - 1, height - 1);
            int chunk = width * 8;
            for (int off = 0; off < rgb565.Count; off += chunk)
                _port.Write(rgb565.Array!, rgb565.Offset + off, Math.Min(chunk, rgb565.Count - off));
        }
    }

    /// <summary>Fecha e solta a porta. A trava espera um quadro em andamento: nada escreve numa porta já fechada.</summary>
    public void CloseSafe()
    {
        lock (_gate)
        {
            try { _port.Close(); } catch (Exception ex) { Infrastructure.AppLog.Debug("lcd", ex); }
            _port.Dispose();
        }
    }

    public void Dispose() => CloseSafe();
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
    private readonly object _gate = new();
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
        return LcdPortOpener.Open(com, () => LcdPortOpener.Using(NewPort(com), candidate =>
        {
            candidate.Open();
            var t = new TuringRevC(candidate, com);
            t.Init(brightness);
            return t;
        }));
    }

    // Buffers reaproveitados na atualização contínua: mensagem de comando, quadro com os separadores e a confirmação de 1024 bytes.
    private byte[] _msg = new byte[250];
    private byte[]? _frame;
    private readonly byte[] _ack = new byte[1024];
    private static readonly byte[] Cmd2C = { 0x2C }, Bitmap480Count = { 0x0E, 0x10 };

    private void Send(byte[] cmd, byte[]? payload = null, byte pad = 0x00)
    {
        int len = cmd.Length + (payload?.Length ?? 0);
        int total = (len + 249) / 250 * 250;
        if (_msg.Length < total) _msg = new byte[total];
        var msg = _msg;
        cmd.CopyTo(msg, 0);
        payload?.CopyTo(msg, cmd.Length);
        msg.AsSpan(len, total - len).Fill(pad);
        _port.Write(msg, 0, total);
    }

    /// <summary>Lê e descarta até n bytes (confirmações), sem alocar.</summary>
    private void Drain(int n)
    {
        int got = 0;
        try { while (got < n) { int r = _port.Read(_ack, got, Math.Min(n, _ack.Length) - got); if (r <= 0) break; got += r; } }
        catch (TimeoutException) { }
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
            // Não é IOException: a tela já respondeu 5 vezes sem se identificar, abrir a porta de novo não resolve.
            if (attempt == 4) throw new InvalidDataException($"A tela não se identificou (respondeu \"{Id}\").");
            Thread.Sleep(1000);
        }
        Send(StopVideo);
        Send(StopMedia); Read(1024);
        SetBrightness(brightness);
        Send(Options, new byte[] { 0x00, 0x00, 0x00, 0x00 }); // início padrão, sem inverter, sem desligar sozinha
    }

    /// <summary>Brilho 0–100 (255 = mais claro).</summary>
    public void SetBrightness(int percent)
    {
        lock (_gate) Send(SetBrightnessCmd, new[] { (byte)(Math.Clamp(percent, 0, 100) * 255 / 100) });
    }

    /// <summary>Quadro inteiro 480×480 em BGRA (4 bytes por pixel).</summary>
    public void SendFrame480(ArraySegment<byte> bgra)
    {
        if (bgra.Count != 480 * 480 * 4) throw new ArgumentException("A imagem precisa ter 480×480.");
        lock (_gate) WriteFrame480(bgra);
    }

    private void WriteFrame480(ArraySegment<byte> bgra)
    {
        // Um 0x00 depois de cada 249 bytes de imagem (o protocolo separa blocos de 250), completado com zeros até
        // múltiplo de 250. O tamanho é sempre o mesmo, então o buffer é criado uma vez e separadores/final continuam zerados.
        int chunks = (bgra.Count + 248) / 249;
        int len = bgra.Count + chunks - 1, total = (len + 249) / 250 * 250;
        var data = _frame ??= new byte[total];
        var src = bgra.AsSpan();
        for (int i = 0, o = 0; i < src.Length; i += 249)
        {
            int n = Math.Min(249, src.Length - i);
            src.Slice(i, n).CopyTo(data.AsSpan(o));
            o += n;
            if (i + n < src.Length) data[o++] = 0x00;
        }
        Send(PreUpdate);
        Send(Cmd2C, null, 0x2C);
        Send(DisplayBitmap2Inch, Bitmap480Count); // 480×480/64 = 3600
        _port.Write(data, 0, total);
        Drain(1024);
        Send(QueryStatus); Drain(1024);
    }

    /// <summary>Fecha e solta a porta. A trava espera um quadro em andamento: nada escreve numa porta já fechada.</summary>
    public void CloseSafe()
    {
        lock (_gate)
        {
            try { _port.Close(); } catch (Exception ex) { Infrastructure.AppLog.Debug("lcd", ex); }
            _port.Dispose();
        }
    }

    public void Dispose() => CloseSafe();
}
