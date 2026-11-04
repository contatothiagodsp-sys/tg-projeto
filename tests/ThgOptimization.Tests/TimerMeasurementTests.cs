using ThgOptimization.Core.Performance;

namespace ThgOptimization.Tests;

public class TimerMeasurementTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 20, 0, 0);

    private static GameSessionRecord Session(string game, DateTime start, double avg, double low) => new(start, game, 30, avg, low, false);

    [Fact]
    public void PendingTimerMeasurement_null_nao_afeta_begin()
    {
        Assert.True(TimerMeasurement.TimerAllowed(null, "CS2", null));
        Assert.True(TimerMeasurement.TimerAllowed(null, "CS2", TimerMeasurement.Default));
    }

    [Fact]
    public void Jogo_decidido_como_off_nao_pede_o_timer()
    {
        Assert.False(TimerMeasurement.TimerAllowed(null, "CS2", TimerMeasurement.Off));
    }

    [Fact]
    public void Medicao_vale_so_para_o_jogo_escolhido()
    {
        var pending = TimerMeasurement.Arm("CS2", Now);
        Assert.False(TimerMeasurement.TimerAllowed(pending, "CS2", null));
        Assert.True(TimerMeasurement.TimerAllowed(pending, "Dota 2", null));
    }

    [Fact]
    public void Medicao_com_3_sessoes_liga_o_timer_e_marca_pendente()
    {
        Assert.False(TimerMeasurement.ShouldOffer(null, null, sessionsOfGame: 2, timerEnabled: true));
        Assert.True(TimerMeasurement.ShouldOffer(null, null, sessionsOfGame: 3, timerEnabled: true));
        Assert.False(TimerMeasurement.ShouldOffer(null, null, sessionsOfGame: 3, timerEnabled: false));
        Assert.False(TimerMeasurement.ShouldOffer(null, new GameProfile { TimerMeasureOffered = true }, 3, true));

        var pending = TimerMeasurement.Arm("CS2", Now);
        Assert.True(pending.TimerOff);
        Assert.Equal("session", pending.BenchmarkKind);
        Assert.False(TimerMeasurement.ShouldOffer(pending, null, 9, true));
    }

    [Fact]
    public void Segunda_sessao_sem_timer_compara_e_persiste()
    {
        var s = new AppSettings { PendingTimerMeasurement = TimerMeasurement.Arm("CS2", Now) };
        s.GameSessions.Add(Session("CS2", Now.AddDays(-1), 110, 80));
        s.GameSessions.Add(Session("CS2", Now.AddDays(-2), 110, 80));
        var without = Session("CS2", Now.AddHours(1), 100, 70);
        s.GameSessions.Insert(0, without);

        string? message = TimerMeasurement.Conclude(s, "steam:730", without);

        Assert.Contains("+10", message);
        Assert.Contains("medido neste PC", message);
        Assert.Null(s.PendingTimerMeasurement);
        Assert.Equal(TimerMeasurement.Default, s.GameProfiles["steam:730"].TimerResolution);
        Assert.True(s.GameProfiles["steam:730"].TimerMeasureOffered);
    }

    [Fact]
    public void Ganho_abaixo_de_1_por_cento_desliga_por_padrao()
    {
        var s = new AppSettings { PendingTimerMeasurement = TimerMeasurement.Arm("CS2", Now) };
        s.GameSessions.Add(Session("CS2", Now.AddDays(-1), 100.5, 70.5));
        var without = Session("CS2", Now.AddHours(1), 100, 70);

        string? message = TimerMeasurement.Conclude(s, "steam:730", without);

        Assert.Contains("sem ganho mensurável", message);
        Assert.Equal(TimerMeasurement.Off, s.GameProfiles["steam:730"].TimerResolution);
    }

    [Fact]
    public void Ganho_so_no_low_tambem_mantem_o_timer()
    {
        var r = TimerMeasurement.Compare(new[] { Session("x", Now, 100, 75) }, Session("x", Now, 100, 70))!;
        Assert.True(r.KeepTimer);
    }

    /// <summary>Sem timer deu mais FPS: é perda, não "diferença". Não pode virar motivo para manter ligado.</summary>
    [Fact]
    public void Sessao_sem_timer_melhor_nao_mantem_o_timer()
    {
        var r = TimerMeasurement.Compare(new[] { Session("x", Now, 90, 60) }, Session("x", Now, 100, 70))!;
        Assert.False(r.KeepTimer);
    }

    [Fact]
    public void Outra_sessao_nao_conclui_a_medicao()
    {
        var s = new AppSettings { PendingTimerMeasurement = TimerMeasurement.Arm("CS2", Now) };
        Assert.Null(TimerMeasurement.Conclude(s, "k", Session("Dota 2", Now.AddHours(1), 100, 70)));
        Assert.NotNull(s.PendingTimerMeasurement);
    }

    [Fact]
    public void Sem_sessao_anterior_descarta_a_medicao_sem_decidir()
    {
        var s = new AppSettings { PendingTimerMeasurement = TimerMeasurement.Arm("CS2", Now) };
        Assert.Null(TimerMeasurement.Conclude(s, "k", Session("CS2", Now.AddHours(1), 100, 70)));
        Assert.Null(s.PendingTimerMeasurement);
        Assert.Empty(s.GameProfiles);
    }
}
