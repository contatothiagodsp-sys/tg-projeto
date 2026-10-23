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

    public string Name => "OpenRGB";
    public int MinIntervalMs => 25;
    public int ProtocolVersion => _proto;
    /// <summary>A última tentativa não achou servidor na porta (recusada ou sem resposta): vale tentar abrir o OpenRGB.</summary>
    public bool LastRefused { get; private set; }

    private const uint ReqControllerCount = 0, ReqControllerData = 1, ReqProtocolVersion = 40, SetClientName = 50,
                       UpdateMode = 1101, UpdateLeds = 1050, SetCustomMode = 1100;

    // O envio de cores roda na thread do LightPump; conectar, assumir e soltar, na janela. Um pacote nunca se mistura com outro.
    private readonly object _wire = new();

    private void Write(uint dev, uint type, byte[] payload)
    {
        lock (_wire) WriteUnlocked(dev, type, payload);
    }

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

    private (string Name, string Vendor, int Leds, int Active, byte[]? ModeBytes) Parse(byte[] data)
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
        for (int m = 0; m < numModes; m++)
        {
            int start = (int)ms.Position;
            Str(r); r.ReadInt32(); r.ReadUInt32();                    // nome, valor, flags
            r.ReadUInt32(); r.ReadUInt32();                           // velocidade mín/máx
            if (_proto >= 3) { r.ReadUInt32(); r.ReadUInt32(); }      // brilho mín/máx
            r.ReadUInt32(); r.ReadUInt32();                           // cores mín/máx
            r.ReadUInt32();                                           // velocidade
            if (_proto >= 3) r.ReadUInt32();                          // brilho
            r.ReadUInt32(); r.ReadUInt32();                           // direção, modo de cor
            int nc = r.ReadUInt16();
            r.ReadBytes(nc * 4);
            if (m == active) activeBytes = data[start..(int)ms.Position];
        }
        int numZones = r.ReadUInt16();
        for (int z = 0; z < numZones; z++)
        {
            Str(r); r.ReadInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
            int matrixLen = r.ReadUInt16();
            if (matrixLen > 0) r.ReadBytes(matrixLen);
        }
        int numLeds = r.ReadUInt16();
        return (name, vendor, numLeds, active, activeBytes);
    }

    /// <summary>Conecta e lista os aparelhos. A conversa com o servidor é síncrona com timeout: chame por Task.Run.</summary>
    public async Task<(List<LightDevice> Devices, string Status)> ConnectAsync(string host, int port)
    {
        Shutdown();
        LastRefused = false;
        var list = new List<LightDevice>();
        try
        {
            var tcp = new TcpClient { NoDelay = true };
            using (var cts = new CancellationTokenSource(2000))
                try { await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { tcp.Dispose(); LastRefused = true; return (list, "o OpenRGB não respondeu na porta " + port + "."); }
            _tcp = tcp;
            _ns = tcp.GetStream();
            _ns.ReadTimeout = 3000;
            _ns.WriteTimeout = 3000;

            Write(0, SetClientName, Encoding.ASCII.GetBytes("tHg Optimization\0"));
            Write(0, ReqProtocolVersion, BitConverter.GetBytes(3u));
            uint server;
            try { server = BitConverter.ToUInt32(ReadType(ReqProtocolVersion), 0); }
            catch (IOException) { Shutdown(); return (list, "o OpenRGB é antigo demais (sem negociação de protocolo). Atualize para a versão 0.9 ou mais nova."); }
            _proto = (int)Math.Min(3u, server);
            if (_proto < 3) { Shutdown(); return (list, $"protocolo {_proto} do OpenRGB é antigo. Atualize para a versão 0.9 ou mais nova."); }

            Write(0, ReqControllerCount, Array.Empty<byte>());
            int count = (int)BitConverter.ToUInt32(ReadType(ReqControllerCount), 0);
            _info.Clear();
            for (int i = 0; i < count; i++)
            {
                Write((uint)i, ReqControllerData, BitConverter.GetBytes((uint)_proto));
                var (name, vendor, leds, active, modeBytes) = Parse(ReadType(ReqControllerData));
                if (leds <= 0) continue;
                string id = $"{i}:{name}";
                _info[id] = (i, active, modeBytes);
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
            bool refused = ex is AggregateException ae && ae.InnerException is SocketException || ex is SocketException;
            LastRefused = refused;
            return (list, refused
                ? "o OpenRGB não está com o servidor SDK ligado em " + host + ":" + port + "."
                : "falha na conversa com o OpenRGB: " + ex.Message);
        }
    }

    public int Acquire(LightDevice d, bool exclusive)
    {
        try { Write((uint)_info[d.Id].Index, SetCustomMode, Array.Empty<byte>()); return 0; }
        catch { return 1; }
    }

    public int Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        try
        {
            int n = Math.Min(colors.Length, d.LedCount);
            var p = new byte[6 + 4 * n];
            BitConverter.TryWriteBytes(p.AsSpan(0), (uint)p.Length);
            BitConverter.TryWriteBytes(p.AsSpan(4), (ushort)n);
            for (int i = 0; i < n; i++) { p[6 + i * 4] = colors[i].R; p[7 + i * 4] = colors[i].G; p[8 + i * 4] = colors[i].B; }
            Write((uint)_info[d.Id].Index, UpdateLeds, p);
            return 0;
        }
        catch { return 1; }
    }

    /// <summary>Volta o dispositivo ao modo que estava ativo quando conectamos.</summary>
    public void Release(LightDevice d)
    {
        try
        {
            var (idx, active, mode) = _info[d.Id];
            if (mode == null) return;
            var p = new byte[8 + mode.Length];
            BitConverter.TryWriteBytes(p.AsSpan(0), (uint)p.Length);
            BitConverter.TryWriteBytes(p.AsSpan(4), (uint)active);
            mode.CopyTo(p, 8);
            Write((uint)idx, UpdateMode, p);
        }
        catch { }
    }

    public void EndSession() { }
    public string ErrorText(int e) => e == 0 ? "ok" : "falha de comunicação com o OpenRGB";

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
