namespace ThgOptimization.Tests;

public class UiThrottleTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [Theory]
    [InlineData(false, false, 1, true)]  // sem jogo: toda batida envia
    [InlineData(true, true, 1, true)]    // jogo aberto, mas o usuário está no tHg
    [InlineData(true, false, 1, false)]  // jogando: espaça
    [InlineData(true, false, 2, false)]
    [InlineData(true, false, 3, true)]
    [InlineData(true, false, 10, true)]
    public void Telas_LCD(bool game, bool window, int secondsSinceLast, bool due) =>
        Assert.Equal(due, UiThrottle.LcdRoundDue(game, window, T0, T0.AddSeconds(secondsSinceLast)));

    [Fact]
    public void Primeira_rodada_do_jogo_sempre_envia() =>
        Assert.True(UiThrottle.LcdRoundDue(true, false, default, T0));

    [Theory]
    [InlineData(true, false, true, false, true)]   // escondida: nunca desenha
    [InlineData(true, true, false, true, true)]    // escondida, mesmo com gargalo: como antes
    [InlineData(false, true, false, false, true)]  // à vista, sem foco, jogo aberto: não desenha
    [InlineData(false, true, false, true, false)]  // ... mas o gargalo em uso continua
    [InlineData(false, true, true, false, false)]  // jogo aberto com o usuário no tHg
    [InlineData(false, false, false, false, false)] // sem jogo: desenha, mesmo sem foco
    public void Janela(bool hidden, bool game, bool window, bool bottleneck, bool skip) =>
        Assert.Equal(skip, UiThrottle.SkipWindowDraw(hidden, game, window, bottleneck));
}
