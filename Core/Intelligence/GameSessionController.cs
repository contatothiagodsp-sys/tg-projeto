namespace ThgOptimization.Core.Intelligence;

public enum SessionState { Idle, Detecting, Preparing, Gaming, Capturing, Diagnosing, Restoring, Error }

/// <summary>O que a detecção de jogos vê num instante. É o Modo Jogo que observa o processo; o controlador só recebe o resultado.</summary>
public sealed record GameObservation(bool WatchingForGames, int? GamePid, string? GameName, string? GameKey, bool Capturing = false);

/// <summary>Uma sessão de jogo reconhecida pelo controlador.</summary>
public sealed record GameSessionInfo(string SessionId, int Pid, string GameName, string GameKey, DateTime StartedAt)
{
    public DateTime? EndedAt { get; init; }
    public TimeSpan Duration(DateTime now) => (EndedAt ?? now) - StartedAt;
}

/// <summary>O trabalho que o controlador pede ao app em cada transição. Tudo opcional; uma exceção leva ao estado Error e à restauração.</summary>
public sealed class SessionHooks
{
    /// <summary>Jogo detectado: aplicar o que o usuário pré-autorizou e reduzir as tarefas do próprio tHg.</summary>
    public Action<GameSessionInfo>? Prepare { get; init; }
    /// <summary>Jogo encerrado: diagnosticar e aprender.</summary>
    public Action<GameSessionInfo>? Diagnose { get; init; }
    /// <summary>Sempre roda depois de uma sessão (com ou sem erro): desfazer o temporário e retomar tarefas.</summary>
    public Action<GameSessionInfo>? Restore { get; init; }
}

/// <summary>
/// Máquina de estados da sessão de jogo. Garante a ordem (preparar → jogar → diagnosticar → restaurar), que a restauração roda mesmo
/// quando o diagnóstico falha ou o jogo some de repente, e que o app fechando no meio da partida restaura sem diagnosticar.
/// Não toca em processo nenhum: quem detecta o jogo é o Modo Jogo, por interfaces públicas do Windows.
/// </summary>
public sealed class GameSessionController
{
    private readonly SessionHooks _hooks;
    private readonly Func<DateTime> _now;
    private readonly Action<string, Exception>? _log;
    private readonly object _gate = new();
    private bool _shutdown;

    public GameSessionController(SessionHooks hooks, Func<DateTime>? now = null, Action<string, Exception>? log = null)
    {
        _hooks = hooks;
        _now = now ?? (() => DateTime.Now);
        _log = log;
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public GameSessionInfo? Current { get; private set; }
    public GameSessionInfo? Last { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>(de, para). Chega na thread que chamou Observe.</summary>
    public event Action<SessionState, SessionState>? StateChanged;

    private static readonly Dictionary<SessionState, SessionState[]> Allowed = new()
    {
        [SessionState.Idle] = new[] { SessionState.Detecting, SessionState.Preparing },
        [SessionState.Detecting] = new[] { SessionState.Idle, SessionState.Preparing },
        [SessionState.Preparing] = new[] { SessionState.Gaming, SessionState.Error },
        [SessionState.Gaming] = new[] { SessionState.Capturing, SessionState.Diagnosing, SessionState.Restoring },
        [SessionState.Capturing] = new[] { SessionState.Gaming, SessionState.Diagnosing, SessionState.Restoring },
        [SessionState.Diagnosing] = new[] { SessionState.Restoring, SessionState.Error },
        [SessionState.Restoring] = new[] { SessionState.Idle, SessionState.Detecting, SessionState.Error },
        [SessionState.Error] = new[] { SessionState.Restoring, SessionState.Idle, SessionState.Detecting },
    };

    internal static bool CanGo(SessionState from, SessionState to) => Allowed[from].Contains(to);

    /// <summary>Chamado a cada tique com o que a detecção vê. Idempotente: a mesma observação repetida não repete trabalho.</summary>
    public void Observe(GameObservation o)
    {
        lock (_gate)
        {
            if (_shutdown) return;
            bool game = o.GamePid != null && o.GameName != null;
            switch (State)
            {
                case SessionState.Idle:
                case SessionState.Detecting:
                    if (game) Begin(o);
                    else Go(o.WatchingForGames ? SessionState.Detecting : SessionState.Idle);
                    break;
                case SessionState.Gaming:
                case SessionState.Capturing:
                    // Outro PID = o jogo anterior acabou e outro começou entre dois tiques: fecha uma sessão antes de abrir a outra.
                    if (!game || o.GamePid != Current!.Pid) { End(o.WatchingForGames, diagnose: true); if (game) Begin(o); }
                    else Go(o.Capturing ? SessionState.Capturing : SessionState.Gaming);
                    break;
                case SessionState.Error:
                    Go(o.WatchingForGames ? SessionState.Detecting : SessionState.Idle);
                    if (game) Begin(o);
                    break;
            }
        }
    }

    /// <summary>O app está fechando: restaura o que for temporário, sem diagnosticar. Depois disso o controlador ignora observações.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_shutdown) return;
            if (State is SessionState.Gaming or SessionState.Capturing) End(watching: false, diagnose: false);
            _shutdown = true;
        }
    }

    private void Begin(GameObservation o)
    {
        var info = new GameSessionInfo(Guid.NewGuid().ToString("N"), o.GamePid!.Value, o.GameName!, o.GameKey ?? o.GameName!, _now());
        Current = info;
        LastError = null;
        Go(SessionState.Preparing);
        try { _hooks.Prepare?.Invoke(info); }
        catch (Exception ex)
        {
            // Preparar falhou: o que chegou a ser aplicado é desfeito, e a sessão não segue pela metade.
            Fail("preparar", ex);
            RunRestore(info);
            Current = null;
            Go(o.WatchingForGames ? SessionState.Detecting : SessionState.Idle);
            return;
        }
        Go(SessionState.Gaming);
        if (o.Capturing) Go(SessionState.Capturing);
    }

    private void End(bool watching, bool diagnose)
    {
        var info = Current! with { EndedAt = _now() };
        if (diagnose)
        {
            Go(SessionState.Diagnosing);
            try { _hooks.Diagnose?.Invoke(info); }
            catch (Exception ex) { Fail("diagnosticar", ex); }
        }
        RunRestore(info);
        Last = info;
        Current = null;
        Go(watching ? SessionState.Detecting : SessionState.Idle);
    }

    private void RunRestore(GameSessionInfo info)
    {
        Go(SessionState.Restoring);
        try { _hooks.Restore?.Invoke(info); }
        catch (Exception ex) { Fail("restaurar", ex); }
    }

    private void Fail(string step, Exception ex)
    {
        LastError = $"Falha ao {step}: {ex.Message}";
        _log?.Invoke(step, ex);
        Go(SessionState.Error);
    }

    private void Go(SessionState to)
    {
        if (State == to) return;
        if (!CanGo(State, to)) throw new InvalidOperationException($"Transição inválida: {State} → {to}.");
        var from = State;
        State = to;
        try { StateChanged?.Invoke(from, to); }
        catch (Exception ex) { _log?.Invoke("avisar a mudança de estado", ex); }
    }

    public static string Label(SessionState s) => s switch
    {
        SessionState.Idle => "Parado",
        SessionState.Detecting => "Aguardando um jogo",
        SessionState.Preparing => "Preparando a sessão",
        SessionState.Gaming => "Em jogo",
        SessionState.Capturing => "Em jogo · medindo",
        SessionState.Diagnosing => "Analisando a sessão",
        SessionState.Restoring => "Restaurando",
        _ => "Erro",
    };
}
