using System.IO;
using System.Text.RegularExpressions;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

/// <summary>O registro é um só por processo: os testes que escrevem nele ficam em série.</summary>
[Collection(TimerRegistryCollection.Name)]
public class TimerRegistryTests
{
    public TimerRegistryTests() => TimerRegistry.Clear();

    [Fact]
    public void Registry_devolve_apenas_timers_registrados()
    {
        TimerRegistry.Register("a", "x", TimeSpan.FromSeconds(1), true);
        TimerRegistry.Register("b", "x", TimeSpan.FromSeconds(2), false, () => false);
        Assert.Equal(new[] { "a", "b" }, TimerRegistry.Snapshot().Select(e => e.Name));
    }

    [Fact]
    public void Unregister_remove()
    {
        TimerRegistry.Register("a", "x", TimeSpan.FromSeconds(1), true);
        TimerRegistry.Unregister("a");
        TimerRegistry.Unregister("nunca existiu");
        Assert.Empty(TimerRegistry.Snapshot());
    }

    [Fact]
    public void Snapshot_e_estavel_e_nao_lanca()
    {
        TimerRegistry.Register("quebrado", "z", TimeSpan.FromSeconds(1), true, () => throw new InvalidOperationException("timer já foi embora"));
        TimerRegistry.Register("ok", "a", () => TimeSpan.FromSeconds(5), () => true);
        var snapshot = TimerRegistry.Snapshot();
        TimerRegistry.Register("depois", "a", TimeSpan.Zero, true);
        Assert.Equal(new[] { "ok", "quebrado" }, snapshot.Select(e => e.Name)); // ordenado por área, e não muda depois
        Assert.False(snapshot[1].Running);
        Assert.Equal("Parado", snapshot[1].StateText);
        Assert.Equal("5 s", snapshot[0].IntervalText);
    }

    [Fact]
    public void Intervalo_abaixo_de_um_segundo_aparece_em_ms()
    {
        TimerRegistry.Register("a", "x", TimeSpan.FromMilliseconds(250), true);
        Assert.Equal("250 ms", TimerRegistry.Snapshot()[0].IntervalText);
    }

    [Fact]
    public void Pausar_e_retomar_devolve_os_ativos()
    {
        bool on = true, off = false;
        int resumedOff = 0;
        TimerRegistry.RegisterPausable("ligado", "x", TimeSpan.FromSeconds(1), () => on, () => on = false, () => on = true);
        TimerRegistry.RegisterPausable("desligado", "x", TimeSpan.FromSeconds(1), () => off, () => off = false, () => resumedOff++);
        TimerRegistry.Register("essencial", "x", TimeSpan.FromSeconds(1), true, () => true);

        Assert.Equal(1, TimerRegistry.PauseNonEssential());
        Assert.False(on);
        Assert.True(TimerRegistry.AnyPaused);
        Assert.True(TimerRegistry.Snapshot().Single(e => e.Name == "essencial").Running);

        Assert.Equal(1, TimerRegistry.ResumePaused());
        Assert.True(on);
        Assert.Equal(0, resumedOff); // já estava parado antes de pausar: continua parado
        Assert.False(TimerRegistry.AnyPaused);
    }

    private static string RepoText_(string name) => File.ReadAllText(Path.Combine(RepoText.Root, name));

    /// <summary>Todo campo de timer da janela aparece no registro e para no Closed. Timer novo sem registro reprova aqui.</summary>
    [Fact]
    public void Every_timer_registered_is_stopped_on_closed()
    {
        var fields = Directory.EnumerateFiles(RepoText.Root, "MainWindow*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"private (?:readonly )?DispatcherTimer\?? (_\w+)").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.Contains("_timerPanelTimer", fields);

        string register = RepoText_("MainWindow.Timers.cs");
        string window = RepoText_("MainWindow.xaml.cs");
        int start = window.IndexOf("Closed += (_, _) =>", StringComparison.Ordinal);
        string closed = window[start..window.IndexOf("};", start, StringComparison.Ordinal)];
        foreach (string field in fields)
        {
            Assert.True(Regex.IsMatch(register, $@"TimerRegistry\.Register\w*\(.*{field}\b"), $"{field} não está no TimerRegistry.");
            Assert.True(closed.Contains(field + "?.Stop()") || closed.Contains(field + ".Stop()"), $"{field} não para no Closed da janela.");
        }
        Assert.Contains("UnregisterTimers()", closed);
    }

    /// <summary>Timer curto de tarefa: entra com Track e sai com Unregister, senão a lista cresceria a cada benchmark.</summary>
    [Fact]
    public void Local_timers_are_tracked_and_removed()
    {
        foreach (string file in Directory.EnumerateFiles(RepoText.Root, "MainWindow*.cs"))
        {
            string text = File.ReadAllText(file);
            int created = Regex.Matches(text, @"var timer = new DispatcherTimer\(").Count;
            Assert.Equal(created, Regex.Matches(text, @"TimerRegistry\.Track\(").Count);
            Assert.Equal(created, Regex.Matches(text, @"TimerRegistry\.Unregister\(""").Count - (Path.GetFileName(file) == "MainWindow.Timers.cs" ? 1 : 0));
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimerRegistryCollection
{
    public const string Name = "TimerRegistry";
}
