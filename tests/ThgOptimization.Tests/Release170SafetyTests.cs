using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class UiGuardTests
{
    private sealed class Resource : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }

    [Fact]
    public async Task Failing_body_is_reported_and_does_not_escape()
    {
        Exception? seen = null;
        await UiGuard.RunAsync("teste", async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("coleta falhou");
        }, ex => seen = ex);
        Assert.Equal("coleta falhou", seen?.Message);
    }

    [Fact]
    public async Task Successful_body_reports_nothing()
    {
        bool reported = false;
        await UiGuard.RunAsync("teste", () => Task.CompletedTask, _ => reported = true);
        Assert.False(reported);
    }

    [Fact]
    public void Button_stays_disabled_during_the_body_and_comes_back_after_a_failure() => Sta.Run(() =>
    {
        var button = new System.Windows.Controls.Button();
        bool disabledInside = false;
        Exception? seen = null;
        var run = UiGuard.RunButtonAsync(button, "teste", () =>
        {
            disabledInside = !button.IsEnabled;
            throw new InvalidOperationException("coleta falhou");
        }, ex => seen = ex);
        Assert.True(run.IsCompletedSuccessfully);
        Assert.True(disabledInside);
        Assert.True(button.IsEnabled);
        Assert.Equal("coleta falhou", seen?.Message);
    });

    [Fact]
    public async Task Null_button_still_runs_the_body()
    {
        bool ran = false;
        await UiGuard.RunButtonAsync(null, "teste", () => { ran = true; return Task.CompletedTask; }, _ => { });
        Assert.True(ran);
    }

    [Fact]
    public async Task Resource_opened_while_the_app_is_closing_is_released()
    {
        var resource = new Resource();
        var opened = await UiGuard.OpenUnlessExitingAsync("teste", () => resource, () => true);
        Assert.Null(opened);
        Assert.Equal(1, resource.Disposed);
    }

    [Fact]
    public async Task Resource_opened_normally_stays_open()
    {
        var resource = new Resource();
        Assert.Same(resource, await UiGuard.OpenUnlessExitingAsync("teste", () => resource, () => false));
        Assert.Equal(0, resource.Disposed);
    }

    [Fact]
    public async Task Failure_while_opening_returns_null()
    {
        Assert.Null(await UiGuard.OpenUnlessExitingAsync<Resource>("teste", () => throw new System.IO.IOException("sem HID"), () => false));
    }
}

public class WindowExStyleTests
{
    [Fact]
    public void Add_turns_on_the_bits_and_keeps_the_others() => Sta.Run(() =>
    {
        const long ToolWindow = 0x80, NoActivate = 0x8000000;
        using var window = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("thg-teste") { ExtendedWindowStyle = (int)ToolWindow });
        long before = WindowExStyle.Read(window.Handle);
        Assert.Equal(ToolWindow, before & ToolWindow);
        Assert.Equal(0, before & NoActivate);

        WindowExStyle.Add(window.Handle, NoActivate);

        Assert.Equal(before | NoActivate, WindowExStyle.Read(window.Handle));
    });
}

public class ProcessHandleTests
{
    private const int NoSuchPid = int.MaxValue - 3;

    [Fact]
    public void Denied_process_returns_false_without_running_the_action()
    {
        bool ran = false;
        Assert.False(ProcessUtil.CanOpen(NoSuchPid, ProcessUtil.QueryLimited));
        Assert.False(ProcessUtil.WithHandle(NoSuchPid, ProcessUtil.QueryLimited, _ => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void Own_process_opens_and_the_action_gets_a_valid_handle()
    {
        Assert.True(ProcessUtil.CanOpen(Environment.ProcessId, ProcessUtil.QueryLimited));
        Assert.True(ProcessUtil.WithHandle(Environment.ProcessId, ProcessUtil.QueryLimited, h => h != IntPtr.Zero));
    }
}

public class SilentCatchTests
{
    private static string RepoFile(string name)
    {
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "tHgOptimization.csproj"))) return System.IO.Path.Combine(d.FullName, name);
        throw new System.IO.FileNotFoundException(name);
    }

    [Theory]
    [InlineData("GameMode.cs")]
    [InlineData("FrameMonitor.cs")]
    [InlineData("SensorMonitor.cs")]
    [InlineData("OverlayWindow.xaml.cs")]
    [InlineData("FanEngine.cs")]
    public void Hot_path_files_have_no_silent_catch(string file)
    {
        Assert.DoesNotContain("catch { }", System.IO.File.ReadAllText(RepoFile(file)));
    }

    /// <summary>Timer em campo que não para ao fechar continua batendo em subsistemas já desligados.</summary>
    [Fact]
    public void Every_timer_field_of_the_main_window_stops_when_it_closes()
    {
        string root = System.IO.Path.GetDirectoryName(RepoFile("tHgOptimization.csproj"))!;
        var fields = System.IO.Directory.EnumerateFiles(root, "MainWindow*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(System.IO.File.ReadAllText(f), @"private (?:readonly )?DispatcherTimer\?? (_\w+)").Select(m => m.Groups[1].Value))
            .ToList();
        Assert.Contains("_lcdLoopTimer", fields);

        string text = System.IO.File.ReadAllText(RepoFile("MainWindow.xaml.cs"));
        int start = text.IndexOf("Closed += (_, _) =>", StringComparison.Ordinal);
        string closed = text[start..text.IndexOf("};", start, StringComparison.Ordinal)];
        foreach (string field in fields)
            Assert.True(closed.Contains(field + "?.Stop()") || closed.Contains(field + ".Stop()"), $"{field} não para no Closed da janela.");
    }

    [Fact]
    public void Light_pump_has_no_silent_catch()
    {
        string text = System.IO.File.ReadAllText(RepoFile("LightBackends.cs"));
        Assert.DoesNotContain("catch { }", text[text.IndexOf("class LightPump", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Debug_log_stays_off_the_disk_unless_enabled()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"thg-debug-{Guid.NewGuid():N}.log");
        AppLog.UseFile(path);
        bool was = AppLog.DebugEnabled;
        try
        {
            AppLog.DebugEnabled = false;
            AppLog.Debug("teste", new InvalidOperationException("esperado"));
            Assert.False(System.IO.File.Exists(path));
            AppLog.Warn("teste", new InvalidOperationException("aviso"));
            Assert.Contains("AVISO [teste]", System.IO.File.ReadAllText(path));
        }
        finally
        {
            AppLog.DebugEnabled = was;
            AppLog.UseFile(null!);
            System.IO.File.Delete(path);
        }
    }
}
