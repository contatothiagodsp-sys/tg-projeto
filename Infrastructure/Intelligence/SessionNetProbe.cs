using System.Net;
using System.Net.NetworkInformation;

namespace ThgOptimization.Infrastructure.Intelligence;

/// <summary>
/// Sonda de rede da sessão de jogo: um ICMP a cada 5 s para o roteador (gateway padrão). Se o roteador não responde a ping, usa um
/// servidor público de referência. Só mede: não altera DNS, rotas, firewall nem TCP/IP, e não envia nenhum dado do usuário.
/// O custo é um pacote de 32 bytes a cada 5 s.
/// </summary>
public sealed class SessionNetProbe : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    internal static readonly IPAddress Anchor = IPAddress.Parse("1.1.1.1");
    private const int TimeoutMs = 1000, GatewayMissesToSwitch = 3;

    private readonly Func<IPAddress, int, Task<(bool Ok, long Ms)>> _ping;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private (double? Ms, bool Lost)? _pending;

    /// <summary>Para onde as sondas estão indo, para o relatório dizer o que foi medido.</summary>
    public string Target { get; private set; } = "";

    public SessionNetProbe(Func<IPAddress, int, Task<(bool Ok, long Ms)>>? ping = null) => _ping = ping ?? SystemPing;

    private static async Task<(bool Ok, long Ms)> SystemPing(IPAddress address, int timeoutMs)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);
        return (reply.Status == IPStatus.Success, reply.RoundtripTime);
    }

    /// <summary>Gateway IPv4 da interface ativa. Null = sem rede ou sem gateway.</summary>
    internal static IPAddress? DefaultGateway()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var g in nic.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) return g.Address;
            }
        }
        catch (NetworkInformationException ex) { AppLog.Debug("rede", ex); }
        return null;
    }

    public void Start(IPAddress? gateway = null, TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => Loop(gateway ?? DefaultGateway(), interval ?? Interval, ct), CancellationToken.None);
        }
    }

    private async Task Loop(IPAddress? gateway, TimeSpan interval, CancellationToken ct)
    {
        var target = gateway ?? Anchor;
        Target = gateway != null ? "roteador" : "servidor público de referência";
        int misses = 0;
        bool answeredOnce = false;
        try
        {
            using var timer = new PeriodicTimer(interval);
            do
            {
                (bool Ok, long Ms) r;
                try { r = await _ping(target, TimeoutMs).ConfigureAwait(false); }
                catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException) { r = (false, 0); }
                if (ct.IsCancellationRequested) break;
                if (r.Ok) { answeredOnce = true; misses = 0; }
                else if (!answeredOnce && ++misses >= GatewayMissesToSwitch && !target.Equals(Anchor))
                {
                    // Roteador que nunca respondeu: muitos bloqueiam ICMP. Isso não é perda de pacote; troca o alvo e não conta as tentativas.
                    target = Anchor;
                    Target = "servidor público de referência";
                    misses = 0;
                    continue;
                }
                else if (!answeredOnce) continue; // ainda decidindo o alvo: nada é registrado como perda
                lock (_gate) _pending = r.Ok ? (r.Ms, false) : (null, true);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>A sonda mais recente ainda não consumida. Cada sonda entra em um único segundo da linha do tempo.</summary>
    public bool TryTake(out double? latencyMs, out bool lost)
    {
        lock (_gate)
        {
            if (_pending is not { } p) { latencyMs = null; lost = false; return false; }
            _pending = null;
            latencyMs = p.Ms;
            lost = p.Lost;
            return true;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task loop;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            loop = _loop;
            _pending = null;
        }
        if (cts == null) return;
        cts.Cancel();
        try { loop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        cts.Dispose();
    }

    public void Dispose() => Stop();
}
