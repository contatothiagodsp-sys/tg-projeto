using System.Diagnostics;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task Output_comes_back()
    {
        string output = await ProcessRunner.RunAsync("cmd.exe", "/c echo tudo certo", TimeSpan.FromSeconds(10));
        Assert.Contains("tudo certo", output);
    }

    [Fact]
    public async Task Stuck_program_times_out_and_is_killed()
    {
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync("ping.exe", "-n 60 127.0.0.1", TimeSpan.FromMilliseconds(300)));
        // O ping sozinho levaria 60 s. Folga larga: no runner do GitHub, com os outros testes em paralelo, já levou quase 8 s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Levou {sw.Elapsed}.");
    }

    [Fact]
    public void Default_deadline_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ProcessRunner.DefaultTimeout);
    }

    [Fact]
    public void SecurityAudit_returns_output_and_exit_code()
    {
        var (code, stdout, stderr) = SecurityAudit.RunSplit("cmd.exe", "/c echo tudo certo& echo aviso 1>&2& exit /b 3", 10_000);
        Assert.Equal(3, code);
        Assert.Contains("tudo certo", stdout);
        Assert.Contains("aviso", stderr);
    }

    [Fact]
    public void SecurityAudit_slow_command_times_out_and_is_killed()
    {
        var sw = Stopwatch.StartNew();
        var (code, stdout, stderr) = SecurityAudit.RunSplit("ping.exe", "-n 60 127.0.0.1", 300);
        Assert.Equal((-1, "", "tempo esgotado"), (code, stdout, stderr));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Levou {sw.Elapsed}."); // o ping sozinho levaria 60 s
    }

    /// <summary>O caso que travava para sempre: o programa sai na hora, e o filho que herdou o stdout segura o pipe aberto.</summary>
    [Fact]
    public void SecurityAudit_does_not_wait_for_a_child_holding_the_output_open()
    {
        var sw = Stopwatch.StartNew();
        var (code, stdout, stderr) = SecurityAudit.RunSplit("cmd.exe", "/c start /b ping.exe -n 20 127.0.0.1 & exit /b 0", 800);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Levou {sw.Elapsed}."); // o filho sozinho seguraria o pipe por 20 s
        Assert.Equal(0, code); // o próprio programa terminou bem: o prazo só cortou a leitura da saída
        Assert.Equal("", stdout);
        Assert.Equal("tempo esgotado", stderr);
    }

    [Fact]
    public void SecurityAudit_inside_a_single_threaded_context_does_not_deadlock()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                Assert.Contains("ok", SecurityAudit.Run("cmd.exe", "/c echo ok", 10_000).Output);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(20)), "Travou na thread da janela.");
        Assert.Null(error);
    }

    [Fact]
    public void Sync_call_inside_a_single_threaded_context_does_not_deadlock()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                Assert.Contains("ok", ProcessRunner.Run("cmd.exe", "/c echo ok", TimeSpan.FromSeconds(10)));
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(20)), "Travou na thread da janela.");
        Assert.Null(error);
    }
}
