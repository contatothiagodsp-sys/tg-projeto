namespace ThgOptimization;

/// <summary>O que a janela e as telas LCD deixam de fazer enquanto um jogo roda com o tHg em segundo plano.</summary>
internal static class UiThrottle
{
    /// <summary>Intervalo das telas LCD em atualização contínua durante o jogo (fora dele, 1 s).</summary>
    public static readonly TimeSpan LcdGameInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// O timer das telas LCD bate a cada 1 s: com jogo aberto e o tHg sem foco, só uma rodada a cada 3 s envia. Atrasos do
    /// timer não atrapalham: 3 batidas sempre somam 3 s ou mais. Sem jogo, ou com a janela em uso, toda batida envia.
    /// </summary>
    public static bool LcdRoundDue(bool gameActive, bool windowActive, DateTime last, DateTime now) =>
        !gameActive || windowActive || now - last >= LcdGameInterval;

    /// <summary>
    /// Pular o desenho da janela numa leitura em modo leve? Sempre se ela está escondida; com jogo aberto, também se está à
    /// vista sem foco (no segundo monitor). Medição de gargalo em andamento continua, porque essa leitura alimenta a análise.
    /// </summary>
    public static bool SkipWindowDraw(bool hidden, bool gameActive, bool windowActive, bool bottleneckInUse) =>
        hidden || (gameActive && !windowActive && !bottleneckInUse);
}
