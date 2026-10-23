using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ThgOptimization;

public sealed record ServerPing(string Name, string Note, double? Ms, double? Jitter)
{
    public string Value => Ms is double m ? $"{m:0} ms · variação {Jitter:0.#} ms" : "sem resposta";
    public string Level => Ms is not double m ? "—" : m < 35 ? "ÓTIMO" : m < 70 ? "BOM" : m < 120 ? "MÉDIO" : "ALTO";
}

public sealed record DnsResult(string Name, string Server, double? Ms, bool Current)
{
    public string Value => Ms is double m ? $"{m:0} ms" + (Current ? " · em uso" : "") : "sem resposta";
}

/// <summary>
/// Rede para jogos: tempo até datacenters onde ficam servidores de jogos (conexão TCP, que passa por firewalls que
/// bloqueiam ping) e comparação de servidores DNS (consulta DNS real por UDP, medida em ms), com troca reversível.
/// </summary>
public static class NetExtra
{
    private static readonly (string Name, string Host, string Note)[] Servers =
    {
        ("AWS São Paulo", "ec2.sa-east-1.amazonaws.com", "Amazon no Brasil: onde ficam servidores de muitos jogos online"),
        ("Google Cloud (mais próximo)", "www.google.com", "Rede do Google: mostra a qualidade da rota do seu provedor"),
        ("Cloudflare (mais próximo)", "1.1.1.1", "Rede da Cloudflare: referência de latência do provedor"),
        ("AWS Virgínia, EUA", "ec2.us-east-1.amazonaws.com", "Servidores norte-americanos (jogos sem servidor no Brasil)"),
        ("AWS Frankfurt, Europa", "ec2.eu-central-1.amazonaws.com", "Servidores europeus"),
    };

    public static async Task<List<ServerPing>> PingServersAsync(CancellationToken ct)
    {
        var tasks = Servers.Select(async s =>
        {
            var times = new List<double>();
            for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++)
            {
                if (await TcpConnectMs(s.Host, 443, ct) is double ms) times.Add(ms);
                await Task.Delay(150, ct);
            }
            if (times.Count == 0) return new ServerPing(s.Name, s.Note, null, null);
            times.Sort();
            double med = times[times.Count / 2];
            double jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(b - a)).Average() : 0;
            return new ServerPing(s.Name, s.Note, med, jitter);
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>Tempo do aperto de mão TCP (ida e volta), sem enviar dados.</summary>
    private static async Task<double?> TcpConnectMs(string host, int port, CancellationToken ct)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct);
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip == null) return null;
            using var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(2000);
            var sw = Stopwatch.StartNew();
            await c.ConnectAsync(ip, port, timeout.Token);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ DNS

    public static readonly (string Name, string Primary, string Secondary)[] PublicDns =
    {
        ("Cloudflare", "1.1.1.1", "1.0.0.1"),
        ("Google", "8.8.8.8", "8.8.4.4"),
        ("Quad9", "9.9.9.9", "149.112.112.112"),
        ("OpenDNS", "208.67.222.222", "208.67.220.220"),
    };

    private static readonly string[] TestDomains =
        { "steampowered.com", "riotgames.com", "epicgames.com", "discord.com", "youtube.com", "twitch.tv", "ea.com", "battle.net" };

    /// <summary>Mede cada DNS com consultas reais (mediana de 2 rodadas em 8 domínios de jogos e serviços).</summary>
    public static async Task<List<DnsResult>> BenchmarkDnsAsync(CancellationToken ct)
    {
        var current = CurrentDns();
        var servers = PublicDns.Select(d => (d.Name, d.Primary)).ToList();
        foreach (var c in current.Where(c => servers.All(s => s.Primary != c)))
            servers.Insert(0, (IPAddress.TryParse(c, out var ip) && IsPrivate(ip) ? "Roteador / provedor" : "Provedor", c));

        var results = new List<DnsResult>();
        foreach (var (name, server) in servers)
        {
            var times = new List<double>();
            for (int round = 0; round < 2; round++)
                foreach (var d in TestDomains)
                    if (await QueryMs(server, d, ct) is double ms) times.Add(ms);
            times.Sort();
            results.Add(new DnsResult(name, server, times.Count >= 4 ? times[times.Count / 2] : null, current.Contains(server)));
        }
        return results.OrderBy(r => r.Ms ?? double.MaxValue).ToList();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Consulta DNS do tipo A por UDP, montada à mão (cabeçalho + pergunta), e devolve o tempo de resposta.</summary>
    private static async Task<double?> QueryMs(string server, string domain, CancellationToken ct)
    {
        try
        {
            var q = new List<byte> { (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in domain.Split('.')) { q.Add((byte)label.Length); q.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
            q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            using var udp = new UdpClient();
            udp.Connect(IPAddress.Parse(server), 53);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(q.ToArray(), timeout.Token);
            var resp = await udp.ReceiveAsync(timeout.Token);
            double ms = sw.Elapsed.TotalMilliseconds;
            return resp.Buffer.Length >= 12 && resp.Buffer[0] == q[0] && resp.Buffer[1] == q[1] ? ms : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ trocar o DNS do adaptador

    private static NetworkInterface? ActiveNic() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
        .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0).FirstOrDefault();

    public static List<string> CurrentDns() =>
        ActiveNic()?.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList()
        ?? new List<string>();

    /// <summary>Usa o DNS escolhido no adaptador ativo. Guarda como estava (automático ou os endereços fixos) para voltar.</summary>
    public static string SetDns(string primary, string secondary, AppSettings st)
    {
        var nic = ActiveNic();
        if (nic == null) return "Nenhuma conexão ativa encontrada.";
        if (!IPAddress.TryParse(primary, out _) || !IPAddress.TryParse(secondary, out _) || nic.Name.Contains('"')) return "Endereço inválido.";
        if (!st.TweakBackup.ContainsKey("net.dns"))
        {
            bool dhcpDns = IsDnsAutomatic(nic);
            st.TweakBackup["net.dns"] = $"{nic.Name}|" + (dhcpDns ? "dhcp" : string.Join(",", CurrentDns()));
        }
        var (c1, o1) = SecurityAudit.Run("netsh.exe", $"interface ipv4 set dnsservers name=\"{nic.Name}\" source=static address={primary} register=primary validate=no", 20_000);
        if (c1 != 0) return o1.Trim();
        SecurityAudit.Run("netsh.exe", $"interface ipv4 add dnsservers name=\"{nic.Name}\" address={secondary} index=2 validate=no", 20_000);
        SecurityAudit.Run("ipconfig.exe", "/flushdns", 10_000);
        st.Save();
        return "";
    }

    public static string RestoreDns(AppSettings st)
    {
        if (st.TweakBackup.GetValueOrDefault("net.dns") is not string prev) return "O DNS não foi alterado pelo tHg.";
        var parts = prev.Split('|', 2);
        string name = parts[0], how = parts.Length > 1 ? parts[1] : "dhcp";
        // Valores vindos do settings.json: nome de adaptador sem aspas e só endereços IP de verdade.
        if (name.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) return "Cópia do DNS inválida.";
        if (how != "dhcp" && how.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(a => !IPAddress.TryParse(a, out _))) return "Cópia do DNS inválida.";
        if (how == "dhcp") SecurityAudit.Run("netsh.exe", $"interface ipv4 set dnsservers name=\"{name}\" source=dhcp", 20_000);
        else
        {
            var list = how.Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < list.Length; i++)
                SecurityAudit.Run("netsh.exe", i == 0
                    ? $"interface ipv4 set dnsservers name=\"{name}\" source=static address={list[0]} register=primary validate=no"
                    : $"interface ipv4 add dnsservers name=\"{name}\" address={list[i]} index={i + 1} validate=no", 20_000);
        }
        SecurityAudit.Run("ipconfig.exe", "/flushdns", 10_000);
        st.TweakBackup.Remove("net.dns");
        st.Save();
        return "";
    }

    /// <summary>DNS automático (do roteador) ou fixo, pelo registro do adaptador (NameServer vazio = automático).</summary>
    private static bool IsDnsAutomatic(NetworkInterface nic)
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.Id}");
        return string.IsNullOrWhiteSpace(k?.GetValue("NameServer") as string);
    }
}
