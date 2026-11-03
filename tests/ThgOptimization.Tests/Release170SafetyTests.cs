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
