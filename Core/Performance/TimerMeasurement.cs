namespace ThgOptimization.Core.Performance;

/// <summary>
/// Medição que atravessa uma sessão de jogo ou um reinício. É um objeto simples e imutável: a cópia para gravar pode dividir a mesma instância.
/// Kind: "timer" (uma sessão sem o timer de 0,5 ms), "hvci" ou "hags" (mesmo benchmark antes e depois de mudar o Windows).
/// </summary>
public sealed record PendingMeasurement
{
    public string Kind { get; init; } = "";
    /// <summary>Jogo medido (Kind "timer").</summary>
    public string? Game { get; init; }
    /// <summary>A próxima sessão deste jogo roda sem o timer.</summary>
    public bool TimerOff { get; init; }
    /// <summary>"session" para o timer; senão, o BenchRecord.Test que precisa ser repetido.</summary>
    public string BenchmarkKind { get; init; } = "";
    public double? ScoreBefore { get; init; }
    /// <summary>A partir de quando vale o "depois". Sessões e notas anteriores são o "antes".</summary>
    public DateTime Timestamp { get; init; }
    /// <summary>Estado do Windows quando a medição começou. Enquanto não mudar, o benchmark do "depois" não roda.</summary>
    public bool StateAtStart { get; init; }
    /// <summary>Já avisou uma vez que o estado continua igual.</summary>
    public bool Reminded { get; init; }
}

/// <summary>Resultado da comparação, sempre medido nesta máquina: nunca uma promessa para outro PC ou outro jogo.</summary>
public sealed record TimerComparison(double AvgGainPct, double Low1GainPct, bool KeepTimer);

/// <summary>
/// Efeito do timer de 0,5 ms em cada jogo: compara a média das sessões com o timer contra uma sessão sem ele. O ganho varia por jogo, e sem medir não dá para saber.
/// Só decide: quem pede o timer é o <see cref="GameModeEngine"/>, e a pergunta ao usuário é da janela.
/// </summary>
public static class TimerMeasurement
{
    public const int MinSessions = 3;
    public const double MinAvgGainPct = 1, MinLow1GainPct = 3;
    public const string Off = "off", Default = "default";

    /// <summary>Esta sessão pede o timer? Fica sem ele durante a medição do jogo e nos jogos em que a medição não viu ganho.</summary>
    public static bool TimerAllowed(PendingMeasurement? pending, string game, string? profileResolution) =>
        profileResolution != Off && !(pending is { TimerOff: true } && pending.Game == game);

    /// <summary>Oferecer a medição: já há sessões para comparar, nada em andamento, e este jogo ainda não foi perguntado nem decidido.</summary>
    public static bool ShouldOffer(PendingMeasurement? pending, GameProfile? profile, int sessionsOfGame, bool timerEnabled) =>
        timerEnabled && pending == null && sessionsOfGame >= MinSessions && profile?.TimerMeasureOffered != true && profile?.TimerResolution != Off;

    public static PendingMeasurement Arm(string game, DateTime now) =>
        new() { Kind = "timer", Game = game, TimerOff = true, BenchmarkKind = "session", Timestamp = now };

    /// <summary>Null = sem sessão anterior utilizável: não há o que comparar.</summary>
    public static TimerComparison? Compare(IEnumerable<GameSessionRecord> withTimer, GameSessionRecord withoutTimer)
    {
        var before = withTimer.Where(s => s.AvgFps > 0).ToList();
        if (before.Count == 0 || withoutTimer.AvgFps <= 0) return null;
        double avg = before.Average(s => s.AvgFps), low = before.Average(s => s.Low1Fps);
        double avgGain = (avg - withoutTimer.AvgFps) / withoutTimer.AvgFps * 100;
        double lowGain = withoutTimer.Low1Fps > 0 ? (low - withoutTimer.Low1Fps) / withoutTimer.Low1Fps * 100 : 0;
        return new TimerComparison(avgGain, lowGain, avgGain >= MinAvgGainPct || lowGain >= MinLow1GainPct);
    }

    public static string Message(TimerComparison c, string game) => c.KeepTimer
        ? $"Timer de 0,5 ms, medido neste PC: {c.AvgGainPct:+0.0;-0.0;0.0} % de FPS médio e {c.Low1GainPct:+0.0;-0.0;0.0} % no 1% low em {game}. Mantendo ligado."
        : $"Timer de 0,5 ms: sem ganho mensurável em {game}, em uma sessão de comparação. Desligando por padrão para este jogo.";

    /// <summary>
    /// Fecha a medição com a sessão que acabou de ser gravada e guarda a decisão no perfil. Devolve o texto para o usuário, ou null se esta sessão
    /// não era a da medição. Sem sessão anterior utilizável, a medição é descartada e o timer continua como estava.
    /// </summary>
    public static string? Conclude(AppSettings s, string profileKey, GameSessionRecord session)
    {
        var pending = s.PendingTimerMeasurement;
        if (pending is not { TimerOff: true } || pending.Game != session.Game) return null;
        var before = s.CopyList(x => x.GameSessions.Where(r => r.Game == session.Game && r.Start < pending.Timestamp));
        var result = Compare(before, session);
        s.PendingTimerMeasurement = null;
        if (result == null) return null;
        s.Mutate(() =>
        {
            if (!s.GameProfiles.TryGetValue(profileKey, out var profile)) s.GameProfiles[profileKey] = profile = new GameProfile();
            profile.TimerResolution = result.KeepTimer ? Default : Off;
            profile.TimerMeasureOffered = true;
        });
        return Message(result, session.Game);
    }

    /// <summary>Marca que a pergunta foi feita: cada jogo pergunta uma vez só.</summary>
    public static void MarkOffered(AppSettings s, string profileKey)
    {
        s.Mutate(() =>
        {
            if (!s.GameProfiles.TryGetValue(profileKey, out var profile)) s.GameProfiles[profileKey] = profile = new GameProfile();
            profile.TimerMeasureOffered = true;
        });
    }
}
