using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ThgOptimization;

/// <summary>Uma "ponte" para um fabricante/serviço de iluminação (Corsair iCUE, OpenRGB, Razer Chroma...).</summary>
public interface ILightBackend
{
    string Name { get; }
    /// <summary>Intervalo mínimo entre envios para o mesmo dispositivo (0 = a cada quadro).</summary>
    int MinIntervalMs { get; }
    /// <summary>Assume o controle do dispositivo. Devolve 0 se deu certo.</summary>
    int Acquire(LightDevice d, bool exclusive);
    int Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors);
    void Release(LightDevice d);
    /// <summary>Chamado depois de liberar todos os dispositivos desta ponte.</summary>
    void EndSession();
    string ErrorText(int e);
    void Shutdown();
}

/// <summary>Dispositivo de iluminação já traduzido para um formato comum entre as pontes.</summary>
public sealed class LightDevice
{
    public ILightBackend Backend { get; init; } = null!;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>Posição horizontal (0–1) de cada LED; o tamanho do vetor é a quantidade de LEDs.</summary>
    public double[] LedX { get; init; } = Array.Empty<double>();
    public int LedCount => LedX.Length;
    public object? Native { get; init; }
    public string Key => Backend.Name + "|" + Id;
}

// =====================================================================================================================
// Corsair iCUE (usa CorsairSdk.cs)
// =====================================================================================================================
public sealed class CorsairBackend : ILightBackend
{
    private readonly CorsairSdk _sdk;
    public CorsairBackend(CorsairSdk sdk) => _sdk = sdk;

    public string Name => "Corsair iCUE";
    public int MinIntervalMs => 0;

    public List<LightDevice> Wrap(IEnumerable<CorsairDevice> devs) => devs.Select(d => new LightDevice
    {
        Backend = this, Id = d.Id,
        Name = string.IsNullOrWhiteSpace(d.Model) ? d.TypeName : d.Model,
        Detail = $"Corsair · {d.TypeName} · {d.LedCount} LEDs",
        LedX = d.LedX, Native = d,
    }).ToList();

    public int Acquire(LightDevice d, bool exclusive) => _sdk.RequestControl((CorsairDevice)d.Native!, exclusive ? 1 : 0);
    public int Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors) => _sdk.SetColors((CorsairDevice)d.Native!, colors);
    public void Release(LightDevice d) { _sdk.ReleaseControl((CorsairDevice)d.Native!); }
    public void EndSession() { }
    public string ErrorText(int e) => CorsairSdk.ErrorText(e);
    public void Shutdown() { }
}

// =====================================================================================================================
// OpenRGB (servidor SDK em localhost:6742). Cobre MSI Mystic Light, Gigabyte RGB Fusion, ASUS Aura, RAM, Razer, Logitech, etc.
// Protocolo binário little-endian; negociamos a versão 3 do protocolo.
// =====================================================================================================================
public sealed class OpenRgbBackend : ILightBackend
{
    private TcpClient? _tcp;
    private NetworkStream? _ns;
    private int _proto = 3;
    private readonly Dictionary<string, (int Index, int ActiveMode, byte[]? ModeBytes)> _info = new();

    /// <summary>Um modo do controlador como o OpenRGB descreve, com as posições dos campos que mudamos dentro dos bytes.</summary>
    private sealed record OrgbMode(int Index, string Name, uint ColorMode, int BrightnessAt, uint BrightnessMax, int NumColorsAt, int NumColors, byte[] Bytes);
    private readonly Dictionary<string, List<OrgbMode>> _modes = new();
    /// <summary>Modo escolhido para receber nossas cores em cada aparelho (Direct; sem Direct, Static).</summary>
    private readonly Dictionary<string, OrgbMode?> _ctl = new();
    private readonly Dictionary<string, string> _modeNote = new();
    private const uint ColorModePerLed = 1, ColorModeSpecific = 2;

    /// <summary>Qual modo o aparelho recebeu no último Acquire (para o diagnóstico).</summary>
    public string ModeNote(LightDevice d) => _modeNote.TryGetValue(d.Id, out var n) ? n : "";

    public string Name => "OpenRGB";
    public int MinIntervalMs => 25;
    public int ProtocolVersion => _proto;
    /// <summary>A última tentativa não achou servidor na porta (recusada ou sem resposta): vale tentar abrir o OpenRGB.</summary>
    public bool LastRefused { get; private set; }

    private const uint ReqControllerCount = 0, ReqControllerData = 1, ReqProtocolVersion = 40, SetClientName = 50,
                       UpdateMode = 1101, UpdateLeds = 1050, SetCustomMode = 1100;

    // O envio de cores roda na thread do LightPump; conectar, assumir e soltar, na janela. Um pacote nunca se mistura com outro.
    private readonly object _wire = new();

    private void WriteUnlocked(uint dev, uint type, byte[] payload)
    {
        var h = new byte[16];
        "ORGB"u8.CopyTo(h);
        BitConverter.TryWriteBytes(h.AsSpan(4), dev);
        BitConverter.TryWriteBytes(h.AsSpan(8), type);
        BitConverter.TryWriteBytes(h.AsSpan(12), (uint)payload.Length);
        _ns!.Write(h, 0, 16);
        if (payload.Length > 0) _ns.Write(payload, 0, payload.Length);
        _ns.Flush();
    }

    private byte[] ReadExact(int n)
    {
        var b = new byte[n];
        int o = 0;
        while (o < n)
        {
            int r = _ns!.Read(b, o, n - o);
            if (r <= 0) throw new IOException("conexão encerrada pelo OpenRGB");
            o += r;
        }
        return b;
    }

    /// <summary>Lê pacotes até chegar o do tipo pedido (ignora avisos assíncronos do servidor).</summary>
    private byte[] ReadType(uint wanted)
    {
        for (int i = 0; i < 20; i++)
        {
            var h = ReadExact(16);
            if (h[0] != 'O' || h[1] != 'R' || h[2] != 'G' || h[3] != 'B') throw new IOException("resposta inválida (não é o protocolo do OpenRGB)");
            uint type = BitConverter.ToUInt32(h, 8);
            int size = (int)BitConverter.ToUInt32(h, 12);
            if (size < 0 || size > 4_000_000) throw new IOException("pacote grande demais");
            var data = size > 0 ? ReadExact(size) : Array.Empty<byte>();
            if (type == wanted) return data;
        }
        throw new IOException("o OpenRGB não respondeu como esperado");
    }

    private static string Str(BinaryReader r)
    {
        int len = r.ReadUInt16();
        var b = r.ReadBytes(len);
        int n = b.Length;
        while (n > 0 && b[n - 1] == 0) n--;
        return Encoding.UTF8.GetString(b, 0, n);
    }

    private (string Name, string Vendor, int Leds, int Active, byte[]? ModeBytes, List<OrgbMode> Modes) Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32(); // tamanho
        r.ReadInt32();  // tipo do dispositivo
        string name = Str(r);
        string vendor = _proto >= 1 ? Str(r) : "";
        Str(r);                       // descrição
        Str(r); Str(r); Str(r);       // versão, serial, local
        int numModes = r.ReadUInt16();
        int active = r.ReadInt32();
        byte[]? activeBytes = null;
        var modes = new List<OrgbMode>();
        for (int m = 0; m < numModes; m++)
        {
            int start = (int)ms.Position;
            string modeName = Str(r); r.ReadInt32(); r.ReadUInt32();  // nome, valor, flags
            r.ReadUInt32(); r.ReadUInt32();                           // velocidade mín/máx
            uint brMax = 0;
            if (_proto >= 3) { r.ReadUInt32(); brMax = r.ReadUInt32(); } // brilho mín/máx
            r.ReadUInt32(); r.ReadUInt32();                           // cores mín/máx
            r.ReadUInt32();                                           // velocidade
            int brAt = -1;
            if (_proto >= 3) { brAt = (int)ms.Position - start; r.ReadUInt32(); } // brilho
            r.ReadUInt32();                                           // direção
            uint colorMode = r.ReadUInt32();                          // modo de cor
            int ncAt = (int)ms.Position - start;
            int nc = r.ReadUInt16();
            r.ReadBytes(nc * 4);
            var bytes = data[start..(int)ms.Position];
            modes.Add(new OrgbMode(m, modeName, colorMode, brAt, brMax, ncAt, nc, bytes));
            if (m == active) activeBytes = bytes;
        }
        int numZones = r.ReadUInt16();
        for (int z = 0; z < numZones; z++)
        {
            Str(r); r.ReadInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
            int matrixLen = r.ReadUInt16();
            if (matrixLen > 0) r.ReadBytes(matrixLen);
        }
        int numLeds = r.ReadUInt16();
        return (name, vendor, numLeds, active, activeBytes, modes);
    }

    private string _host = "127.0.0.1";
    private int _port = 6742;
    private string _lastError = "";
    private DateTime _lastReconnect;

    /// <summary>Abre o socket, negocia o protocolo e lê todos os controladores. Chame com _wire travado.</summary>
    private List<(int Index, string Name, string Vendor, int Leds, int Active, byte[]? ModeBytes, List<OrgbMode> Modes)> Handshake()
    {
        var tcp = new TcpClient { NoDelay = true };
        using (var cts = new CancellationTokenSource(2000))
            try { tcp.ConnectAsync(_host, _port, cts.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { tcp.Dispose(); throw new TimeoutException("o OpenRGB não respondeu na porta " + _port + "."); }
        _tcp = tcp;
        _ns = tcp.GetStream();
        _ns.ReadTimeout = 3000;
        _ns.WriteTimeout = 3000;

        WriteUnlocked(0, SetClientName, Encoding.ASCII.GetBytes("tHg Optimization\0"));
        WriteUnlocked(0, ReqProtocolVersion, BitConverter.GetBytes(3u));
        uint server;
        try { server = BitConverter.ToUInt32(ReadType(ReqProtocolVersion), 0); }
        catch (IOException) { throw new NotSupportedException("o OpenRGB é antigo demais (sem negociação de protocolo). Atualize para a versão 0.9 ou mais nova."); }
        _proto = (int)Math.Min(3u, server);
        if (_proto < 3) throw new NotSupportedException($"protocolo {_proto} do OpenRGB é antigo. Atualize para a versão 0.9 ou mais nova.");

        WriteUnlocked(0, ReqControllerCount, Array.Empty<byte>());
        int count = (int)BitConverter.ToUInt32(ReadType(ReqControllerCount), 0);
        var list = new List<(int, string, string, int, int, byte[]?, List<OrgbMode>)>();
        for (int i = 0; i < count; i++)
        {
            WriteUnlocked((uint)i, ReqControllerData, BitConverter.GetBytes((uint)_proto));
            var (name, vendor, leds, active, modeBytes, modes) = Parse(ReadType(ReqControllerData));
            list.Add((i, name, vendor, leds, active, modeBytes, modes));
        }
        return list;
    }

    /// <summary>
    /// A conexão caiu (OpenRGB reiniciado, servidor desligado e ligado, nova detecção de aparelhos): conecta de novo e acha
    /// cada aparelho pelo nome, porque os índices mudam quando o OpenRGB refaz a detecção. O modo de antes (para devolver no
    /// fim) continua o da primeira conexão. No máximo uma tentativa a cada 3 s, para não travar o envio de cores.
    /// </summary>
    private bool Reconnect()
    {
        if ((DateTime.Now - _lastReconnect).TotalSeconds < 3) return false;
        _lastReconnect = DateTime.Now;
        Shutdown();
        try
        {
            var ctrls = Handshake();
            var used = new HashSet<int>();
            foreach (var id in _info.Keys.ToList())
            {
                var (oldIdx, active, mode) = _info[id];
                string name = id[(id.IndexOf(':') + 1)..];
                var match = ctrls.Where(c => c.Name == name && c.Leds > 0 && !used.Contains(c.Index))
                                 .OrderBy(c => Math.Abs(c.Index - oldIdx)).Select(c => (int?)c.Index).FirstOrDefault();
                if (match is int ni) { used.Add(ni); _info[id] = (ni, active, mode); }
                else _info.Remove(id);
            }
            return true;
        }
        catch (Exception ex) { _lastError = ex.Message; Shutdown(); return false; }
    }

    /// <summary>Manda um pacote ao aparelho; se a conexão caiu, reconecta uma vez e tenta de novo. 0 = deu certo.</summary>
    private int Do(LightDevice d, uint type, byte[] payload)
    {
        lock (_wire)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!_info.TryGetValue(d.Id, out var info)) { _lastError = "o aparelho sumiu da lista do OpenRGB; clique em conectar de novo"; return 1; }
                try
                {
                    if (_ns == null) throw new IOException("sem conexão com o servidor");
                    WriteUnlocked((uint)info.Index, type, payload);
                    _lastError = "";
                    return 0;
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    _lastError = ex.Message;
                    if (attempt == 0 && !Reconnect()) break;
                }
            }
            return 1;
        }
    }

    /// <summary>Conecta e lista os aparelhos. A conversa com o servidor é síncrona com timeout: chame por Task.Run.</summary>
    public Task<(List<LightDevice> Devices, string Status)> ConnectAsync(string host, int port)
    {
        lock (_wire) return Task.FromResult(ConnectCore(host, port));
    }

    private (List<LightDevice> Devices, string Status) ConnectCore(string host, int port)
    {
        Shutdown();
        LastRefused = false;
        _host = host; _port = port;
        var list = new List<LightDevice>();
        try
        {
            var ctrls = Handshake();
            int count = ctrls.Count;
            _info.Clear(); _modes.Clear(); _ctl.Clear(); _modeNote.Clear();
            foreach (var (i, name, vendor, leds, active, modeBytes, modes) in ctrls)
            {
                if (leds <= 0) continue;
                string id = $"{i}:{name}";
                _info[id] = (i, active, modeBytes);
                _modes[id] = modes;
                var x = new double[leds];
                for (int k = 0; k < leds; k++) x[k] = leds > 1 ? k / (double)(leds - 1) : 0;
                list.Add(new LightDevice
                {
                    Backend = this, Id = id, Name = name,
                    Detail = $"OpenRGB · {(string.IsNullOrWhiteSpace(vendor) ? "—" : vendor)} · {leds} LEDs",
                    LedX = x,
                });
            }
            return (list, $"conectado (protocolo {_proto}) · {list.Count} dispositivo(s) com LEDs de {count}.");
        }
        catch (Exception ex)
        {
            Shutdown();
            bool refused = ex is TimeoutException or SocketException || ex.InnerException is SocketException;
            LastRefused = refused;
            return (list, ex switch
            {
                TimeoutException or NotSupportedException => ex.Message,
                _ when refused => "o OpenRGB não está com o servidor SDK ligado em " + host + ":" + port + ".",
                _ => "falha na conversa com o OpenRGB: " + ex.Message,
            });
        }
    }

    /// <summary>
    /// Põe o aparelho num modo que obedece às nossas cores. Só o SetCustomMode não basta: muitos controladores de fan e
    /// de placa-mãe ignoram o pedido sem avisar e seguem no efeito de hardware (cores "nada a ver"). Por isso escolhemos o
    /// modo pelo nome: Direct (cor por LED); sem ele, Static (cor do modo), já com brilho no máximo.
    /// </summary>
    public int Acquire(LightDevice d, bool exclusive)
    {
        var modes = _modes.TryGetValue(d.Id, out var ms) ? ms : new List<OrgbMode>();
        var pick = modes.FirstOrDefault(m => m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase) && m.ColorMode == ColorModePerLed)
                ?? modes.FirstOrDefault(m => m.Name.Equals("Static", StringComparison.OrdinalIgnoreCase) && m.ColorMode is ColorModePerLed or ColorModeSpecific)
                ?? modes.FirstOrDefault(m => m.ColorMode == ColorModePerLed);
        _ctl[d.Id] = pick;
        if (pick == null)
        {
            _modeNote[d.Id] = "sem modo Direct/Static na lista; tentando o modo personalizado (o aparelho pode ignorar)";
            return Do(d, SetCustomMode, Array.Empty<byte>());
        }
        _modeNote[d.Id] = $"modo \"{pick.Name}\" ({(pick.ColorMode == ColorModePerLed ? "cor por LED" : "cor do modo")})";
        return Do(d, UpdateMode, ModePacket(pick, null));
    }

    /// <summary>Pacote UpdateMode do modo, com brilho no máximo e, se pedido, a cor do modo trocada pela nossa.</summary>
    private static byte[] ModePacket(OrgbMode m, (byte R, byte G, byte B)? color)
    {
        var mode = (byte[])m.Bytes.Clone();
        if (m.BrightnessAt >= 0 && m.BrightnessMax > 0) BitConverter.TryWriteBytes(mode.AsSpan(m.BrightnessAt), m.BrightnessMax);
        if (color is { } c)
            for (int k = 0; k < m.NumColors; k++)
            {
                int o = m.NumColorsAt + 2 + k * 4;
                mode[o] = c.R; mode[o + 1] = c.G; mode[o + 2] = c.B; mode[o + 3] = 0;
            }
        var p = new byte[8 + mode.Length];
        BitConverter.TryWriteBytes(p.AsSpan(0), (uint)p.Length);
        BitConverter.TryWriteBytes(p.AsSpan(4), (uint)m.Index);
        mode.CopyTo(p, 8);
        return p;
    }

    public int Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        // Modo com cor própria (Static sem cor por LED): a cor vai dentro do modo, não pelo UpdateLeds.
        if (_ctl.TryGetValue(d.Id, out var ctl) && ctl is { ColorMode: ColorModeSpecific, NumColors: > 0 } && colors.Length > 0)
            return Do(d, UpdateMode, ModePacket(ctl, colors[0]));
        int n = Math.Min(colors.Length, d.LedCount);
        var p = new byte[6 + 4 * n];
        BitConverter.TryWriteBytes(p.AsSpan(0), (uint)p.Length);
        BitConverter.TryWriteBytes(p.AsSpan(4), (ushort)n);
        for (int i = 0; i < n; i++) { p[6 + i * 4] = colors[i].R; p[7 + i * 4] = colors[i].G; p[8 + i * 4] = colors[i].B; }
        return Do(d, UpdateLeds, p);
    }

    /// <summary>Volta o dispositivo ao modo que estava ativo quando conectamos.</summary>
    public void Release(LightDevice d)
    {
        if (!_info.TryGetValue(d.Id, out var info) || info.ModeBytes is not { } mode) return;
        var p = new byte[8 + mode.Length];
        BitConverter.TryWriteBytes(p.AsSpan(0), (uint)p.Length);
        BitConverter.TryWriteBytes(p.AsSpan(4), (uint)info.ActiveMode);
        mode.CopyTo(p, 8);
        Do(d, UpdateMode, p);
    }

    public void EndSession() { }
    public string ErrorText(int e) => e == 0 ? "ok"
        : "falha de comunicação com o OpenRGB" + (_lastError.Length > 0 ? $" ({_lastError})" : "") +
          ". Feche o MSI Center / Mystic Light, que disputa o controlador, e confira o servidor SDK do OpenRGB";

    public void Shutdown()
    {
        try { _ns?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _ns = null; _tcp = null;
    }
}

// =====================================================================================================================
// Razer Chroma (API REST local do Razer Synapse, porta 54235). Cor única por categoria de dispositivo.
// =====================================================================================================================
public sealed class RazerChromaBackend : ILightBackend
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private string? _uri;
    private long _lastBeat;
    private int _errors;

    private static readonly (string Key, string Name)[] Kinds =
    {
        ("keyboard", "Teclado"), ("mouse", "Mouse"), ("headset", "Headset"), ("mousepad", "Mousepad"),
        ("keypad", "Keypad"), ("chromalink", "Chroma Link (iluminação do case)"),
    };

    public string Name => "Razer Chroma";
    public int MinIntervalMs => 100;
    public int Errors => _errors;

    private async Task<string?> InitAsync()
    {
        var body = JsonSerializer.Serialize(new
        {
            title = "tHg Optimization",
            description = "Controle de iluminação do tHg Optimization",
            author = new { name = "tHg", contact = "https://github.com/GustavoReset/ResetExplode" },
            device_supported = Kinds.Select(k => k.Key).ToArray(),
            category = "application",
        });
        using var resp = await Http.PostAsync("http://localhost:54235/razer/chromasdk", new StringContent(body, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("uri", out var u) ? u.GetString() : null;
    }

    public async Task<(List<LightDevice> Devices, string Status)> ConnectAsync()
    {
        var list = new List<LightDevice>();
        try
        {
            _uri = await InitAsync();
            if (string.IsNullOrEmpty(_uri)) return (list, "o Razer Synapse respondeu, mas não abriu a sessão do Chroma. Ative o Chroma Connect no Synapse.");
            _lastBeat = Environment.TickCount64;
            foreach (var (key, name) in Kinds)
                list.Add(new LightDevice { Backend = this, Id = key, Name = name, Detail = "Razer Chroma · cor única (se você tiver este tipo de dispositivo)", LedX = new[] { 0.0 } });
            return (list, "conectado ao Razer Synapse (Chroma). A lista mostra as categorias; as que você não tem simplesmente não reagem.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (list, "Razer Chroma não encontrado. Instale o Razer Synapse (com o módulo Chroma) e deixe-o aberto.");
        }
        catch (Exception ex) { return (list, "falha no Razer Chroma: " + ex.Message); }
    }

    public int Acquire(LightDevice d, bool exclusive)
    {
        if (_uri != null) return 0;
        // Sem travar a janela: a sessão abre em segundo plano e o Send começa a valer quando ela ficar pronta.
        _init ??= Task.Run(InitAsync);
        _lastBeat = Environment.TickCount64;
        return 0;
    }
    private Task<string?>? _init;

    /// <summary>Pega a sessão aberta em segundo plano pelo Acquire, se já terminou.</summary>
    private void TakeInit()
    {
        if (_uri != null || _init is not { IsCompleted: true } t) return;
        _uri = t.IsCompletedSuccessfully ? t.Result : null; // já terminou: não bloqueia
        _init = null;
        if (_uri == null) Interlocked.Increment(ref _errors);
    }

    public int Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        TakeInit();
        if (_init != null) return 0; // sessão ainda abrindo
        if (_uri == null || colors.Length == 0) return 1;
        int color = colors[0].R | (colors[0].G << 8) | (colors[0].B << 16); // Chroma usa BGR
        string uri = _uri, kind = d.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                var json = "{\"effect\":\"CHROMA_STATIC\",\"param\":{\"color\":" + color + "}}";
                using var r = await Http.PutAsync($"{uri}/{kind}", new StringContent(json, Encoding.UTF8, "application/json"));
            }
            catch { Interlocked.Increment(ref _errors); }
        });
        long now = Environment.TickCount64;
        if (now - _lastBeat > 8000)
        {
            _lastBeat = now;
            _ = Task.Run(async () => { try { using var r = await Http.PutAsync(uri + "/heartbeat", new StringContent("")); } catch { } });
        }
        return 0;
    }

    public void Release(LightDevice d) { }

    /// <summary>Fecha a sessão: o Synapse volta às luzes dele na hora.</summary>
    public void EndSession()
    {
        var uri = _uri;
        _uri = null;
        _init = null;
        if (uri == null) return;
        // Fecha a sessão em segundo plano (antes a janela esperava até 1,5 s por isso).
        _ = Task.Run(async () => { try { using var r = await Http.DeleteAsync(uri); } catch { } });
    }

    public string ErrorText(int e) => e == 0 ? "ok" : "sessão do Razer Chroma indisponível";
    public void Shutdown() => EndSession();
}

// =====================================================================================================================
// Iluminação Dinâmica do Windows (LampArray, HID LampArray). Sem programa do fabricante: o próprio Windows fala com
// os aparelhos (Logitech, Razer, ASUS, teclados e mouses recentes). O Windows só entrega o controle ao app em primeiro
// plano; com o tHg minimizado, as luzes voltam ao efeito de Configurações → Personalização → Iluminação dinâmica.
// =====================================================================================================================
public sealed class DynamicLightingBackend : ILightBackend
{
    private readonly List<Windows.Devices.Lights.LampArray> _open = new();

    public string Name => "Iluminação Dinâmica";
    public int MinIntervalMs => 33;

    public async Task<(List<LightDevice> Devices, string Status)> ConnectAsync()
    {
        Shutdown();
        var list = new List<LightDevice>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return (list, "requer o Windows 10 1809 ou mais novo.");
        try
        {
            var infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Lights.LampArray.GetDeviceSelector());
            foreach (var info in infos)
            {
                Windows.Devices.Lights.LampArray? la;
                try { la = await Windows.Devices.Lights.LampArray.FromIdAsync(info.Id); }
                catch { continue; }
                if (la == null || la.LampCount <= 0) continue;
                _open.Add(la);

                // Posição horizontal de cada LED dentro da caixa do aparelho, para o arco-íris correr de um lado ao outro.
                var x = new double[la.LampCount];
                float width = la.BoundingBox.X;
                for (int i = 0; i < x.Length; i++)
                {
                    float px = la.GetLampInfo(i).Position.X;
                    x[i] = width > 0.0001f ? Math.Clamp(px / width, 0, 1) : (x.Length > 1 ? i / (double)(x.Length - 1) : 0);
                }
                string kind = KindText(la.LampArrayKind);
                list.Add(new LightDevice
                {
                    Backend = this, Id = info.Id,
                    Name = string.IsNullOrWhiteSpace(info.Name) ? kind : info.Name,
                    Detail = $"Iluminação Dinâmica · {kind} · {la.LampCount} LEDs",
                    LedX = x, Native = la,
                });
            }
            return (list, list.Count == 0
                ? "o Windows não listou nenhum aparelho compatível."
                : $"{list.Count} aparelho(s), {list.Sum(d => d.LedCount)} LEDs. O Windows só deixa o tHg controlar as luzes enquanto a janela dele está em primeiro plano.");
        }
        catch (Exception ex) { return (list, "falha ao listar: " + ex.Message); }
    }

    private static string KindText(Windows.Devices.Lights.LampArrayKind k) => k switch
    {
        Windows.Devices.Lights.LampArrayKind.Keyboard => "Teclado",
        Windows.Devices.Lights.LampArrayKind.Mouse => "Mouse",
        Windows.Devices.Lights.LampArrayKind.GameController => "Controle",
        Windows.Devices.Lights.LampArrayKind.Peripheral => "Periférico",
        Windows.Devices.Lights.LampArrayKind.Scene => "Ambiente",
        Windows.Devices.Lights.LampArrayKind.Notification => "Notificação",
        Windows.Devices.Lights.LampArrayKind.Chassis => "Gabinete",
        Windows.Devices.Lights.LampArrayKind.Wearable => "Vestível",
        Windows.Devices.Lights.LampArrayKind.Furniture => "Móvel",
        Windows.Devices.Lights.LampArrayKind.Art => "Decoração",
        // Tipos novos do Windows 11, ausentes neste SDK
        (Windows.Devices.Lights.LampArrayKind)11 => "Headset",
        (Windows.Devices.Lights.LampArrayKind)12 => "Microfone",
        (Windows.Devices.Lights.LampArrayKind)13 => "Caixa de som",
        _ => "Aparelho",
    };

    public int Acquire(LightDevice d, bool exclusive) => d.Native is Windows.Devices.Lights.LampArray ? 0 : 1;

    public int Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        try
        {
            var la = (Windows.Devices.Lights.LampArray)d.Native!;
            int n = Math.Min(colors.Length, la.LampCount);
            var cols = new Windows.UI.Color[n];
            var idx = new int[n];
            for (int i = 0; i < n; i++)
            {
                cols[i] = Windows.UI.Color.FromArgb(255, colors[i].R, colors[i].G, colors[i].B);
                idx[i] = i;
            }
            la.SetColorsForIndices(cols, idx);
            return 0;
        }
        catch { return 1; }
    }

    public void Release(LightDevice d) { }
    public void EndSession() { }
    public string ErrorText(int e) => e == 0 ? "ok" : "o Windows recusou as cores (o aparelho saiu ou o tHg não está em primeiro plano)";

    public void Shutdown() => _open.Clear();
}

/// <summary>
/// Envia as cores numa thread própria, de prioridade baixa: a janela (e o jogo) nunca esperam um dispositivo lento.
/// Guarda só o quadro mais recente de cada dispositivo ("o último vence"): se um aparelho atrasar, quadros velhos são
/// descartados em vez de formar fila.
/// </summary>
public sealed class LightPump : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (LightDevice D, (byte R, byte G, byte B)[] C, Action<int>? Done)> _pending = new();
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _stop;

    public LightPump()
    {
        new Thread(Run) { IsBackground = true, Name = "tHg RGB", Priority = ThreadPriority.BelowNormal }.Start();
    }

    public void Post(LightDevice d, (byte R, byte G, byte B)[] colors, Action<int>? done)
    {
        lock (_gate) _pending[d.Key] = (d, colors, done);
        _signal.Set();
    }

    private void Run()
    {
        var batch = new List<(LightDevice D, (byte R, byte G, byte B)[] C, Action<int>? Done)>();
        while (!_stop)
        {
            _signal.WaitOne(500);
            batch.Clear();
            lock (_gate) { batch.AddRange(_pending.Values); _pending.Clear(); }
            foreach (var (d, c, done) in batch)
            {
                int err;
                try { err = d.Backend.Send(d, c); } catch { err = -1; }
                try { done?.Invoke(err); } catch { }
            }
        }
    }

    public void Dispose() { _stop = true; _signal.Set(); }
}
