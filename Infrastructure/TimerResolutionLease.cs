namespace ThgOptimization.Infrastructure;

/// <summary>
/// Pedido de resolução do timer feito durante uma sessão de jogo. No máximo um pedido em vigor, com exatamente uma devolução.
/// O pedido é do processo: se o tHg fechar ou travar, o Windows o desfaz sozinho.
/// </summary>
public interface ITimerResolutionLease : IDisposable
{
    bool Held { get; }
    /// <summary>Valor em vigor depois do pedido, em unidades de 100 ns. Null = sem pedido.</summary>
    uint? EffectiveResolution { get; }
    /// <summary>Pede a resolução, se ainda não pediu. Devolve o valor em vigor, ou null se o Windows não aceitou.</summary>
    uint? Acquire(uint target100ns, CancellationToken cancel = default);
    /// <summary>Devolve o pedido. Sem pedido em vigor, não faz nada.</summary>
    void Release();
}

/// <summary>Chamadas do ntdll, separadas para os testes contarem os pedidos e as devoluções.</summary>
public interface ITimerResolutionApi
{
    /// <summary>NTSTATUS. Coarsest é o limite mais lento e Finest, o mais fino que o Windows aceita.</summary>
    int Query(out uint coarsest, out uint finest, out uint current);
    int Set(uint desired, bool set, out uint current);
}

public sealed class NtTimerResolutionApi : ITimerResolutionApi
{
    public int Query(out uint coarsest, out uint finest, out uint current) => NativePerfApi.NtQueryTimer(out coarsest, out finest, out current);
    public int Set(uint desired, bool set, out uint current) => NativePerfApi.NtSetTimer(desired, set, out current);
}

/// <summary>
/// Implementação para o Windows. Roda no tick do Modo Jogo, uma vez por sessão: sem thread própria e sem mexer em prioridade.
/// Pedir 0,5 ms não garante 0,5 ms: o limite vem do Windows, o valor efetivo vai para o registro, e nenhum ganho de FPS é prometido.
/// </summary>
public sealed class WindowsTimerResolutionLease : ITimerResolutionLease
{
    private const int StatusNotImplemented = unchecked((int)0xC0000002);

    private readonly ITimerResolutionApi _api;
    private readonly object _gate = new();
    private uint _requested;
    private uint? _effective;
    private bool _disposed;

    public WindowsTimerResolutionLease(ITimerResolutionApi? api = null) => _api = api ?? new NtTimerResolutionApi();

    public bool Held { get { lock (_gate) return _requested != 0; } }
    public uint? EffectiveResolution { get { lock (_gate) return _effective; } }

    /// <summary>Encaixa o alvo no que o Windows aceita. Nos dois limites do ntdll, o número menor é a resolução mais fina.</summary>
    public static uint Clamp(uint target, uint coarsest, uint finest) => Math.Min(Math.Max(target, finest), Math.Max(coarsest, finest));

    public uint? Acquire(uint target100ns, CancellationToken cancel = default)
    {
        lock (_gate)
        {
            if (_disposed || cancel.IsCancellationRequested) return null;
            if (_requested != 0) return _effective;

            uint coarsest = 0, finest = 0, before = 0, current = 0;
            int status;
            try { status = _api.Query(out coarsest, out finest, out before); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { AppLog.Error("timer", ex); status = StatusNotImplemented; }
            if (status != 0 || finest == 0)
            {
                AppLog.Error("timer", $"Limites do timer indisponíveis (0x{status:X8}): nenhum pedido feito.");
                return null;
            }

            uint desired = Clamp(target100ns, coarsest, finest);
            try { status = _api.Set(desired, true, out current); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { AppLog.Error("timer", ex); status = StatusNotImplemented; }
            if (status != 0)
            {
                AppLog.Error("timer", $"O Windows recusou {Ms(desired)} ms (0x{status:X8}).");
                return null;
            }
            _requested = desired;
            _effective = current;
            AppLog.Info("timer", $"Pedido {Ms(desired)} ms, em vigor {Ms(current)} ms (antes {Ms(before)} ms, limites {Ms(finest)} a {Ms(coarsest)} ms).");
            return current;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_requested == 0) return;
            uint desired = _requested;
            _requested = 0; // uma devolução só, mesmo se a chamada falhar: repetir devolveria o pedido de ninguém
            _effective = null;
            int status;
            uint current = 0;
            try { status = _api.Set(desired, false, out current); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { AppLog.Error("timer", ex); status = StatusNotImplemented; }
            if (status == 0) AppLog.Info("timer", $"Pedido de {Ms(desired)} ms devolvido, em vigor {Ms(current)} ms.");
            else AppLog.Error("timer", $"O Windows recusou devolver {Ms(desired)} ms (0x{status:X8}). Ele desfaz ao fechar o tHg.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Release();
            _disposed = true;
        }
    }

    private static string Ms(uint value) => (value / 10000.0).ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
}
