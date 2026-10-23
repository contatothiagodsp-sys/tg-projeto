namespace ThgOptimization;

/// <summary>
/// Leitura do estado dos ajustes fora da thread da janela. Cada Read() pode abrir o powercfg ou o bcdedit, ou consultar o WMI:
/// em série, na janela, a página travava por quase um segundo. Aqui as leituras rodam em paralelo (poucas por vez, só
/// consultas, sem gravar nada), o resultado volta inteiro e a janela aplica tudo numa passada só.
/// </summary>
public static class TweakStates
{
    /// <summary>On = null: a leitura falhou (Error diz o motivo) e o interruptor fica como estava, igual ao Tweak.Refresh().</summary>
    public readonly record struct State(bool? On, string? Error);

    private const int MaxParallel = 4;

    public static Task<State[]> ReadAsync(IReadOnlyList<Tweak> tweaks, CancellationToken ct = default) => Task.Run(() =>
    {
        var states = new State[tweaks.Count];
        Parallel.For(0, tweaks.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = ct }, i =>
        {
            try { states[i] = new State(tweaks[i].Read() ?? false, null); }
            catch (Exception ex) { states[i] = new State(null, Infrastructure.FriendlyError.Of(ex)); }
        });
        return states;
    }, ct);

    /// <summary>Na thread da janela. errorPrefix null: a falha de leitura não muda o Status (como era no TURBO).</summary>
    public static void Apply(IReadOnlyList<Tweak> tweaks, State[] states, string? errorPrefix = "Não foi possível ler: ")
    {
        for (int i = 0; i < tweaks.Count && i < states.Length; i++)
        {
            if (states[i].On is bool on) tweaks[i].IsOn = on;
            else if (errorPrefix != null) tweaks[i].Status = errorPrefix + states[i].Error;
        }
    }
}
