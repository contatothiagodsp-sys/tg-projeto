using System.IO;
using ThgOptimization.Infrastructure;
using Xunit;

namespace ThgOptimization.Tests;

public class ColorOrderAutoTests
{
    [Theory]
    [InlineData("ASRock Polychrome · ARGB 1", "ASRock")]
    [InlineData("Lian Li UNI FAN", "LianLi")]
    [InlineData("NZXT Kraken", "NZXT")]
    [InlineData("Cooler Master ARGB", "CoolerMaster")]
    [InlineData("ASUS ROG STRIX", "ASUS")]
    public void Maker_from_name(string name, string maker)
    {
        Assert.Equal(maker, ColorOrderAuto.DetectMaker("OpenRGB", name, ""));
    }

    [Theory]
    [InlineData((ushort)0x0B05, "ASUS")]
    [InlineData((ushort)0x1462, "MSI")]
    [InlineData((ushort)0x1044, "Gigabyte")]
    [InlineData((ushort)0x1B1C, "Corsair")]
    [InlineData((ushort)0x1532, "Razer")]
    [InlineData((ushort)0x046D, "Logitech")]
    [InlineData((ushort)0x26CE, "ASRock")]
    [InlineData((ushort)0x0CF2, "LianLi")]
    [InlineData((ushort)0x1E71, "NZXT")]
    [InlineData((ushort)0x2516, "CoolerMaster")]
    public void Maker_from_vendor_id(ushort vid, string maker)
    {
        Assert.Equal(maker, ColorOrderAuto.MakerFromVid(vid));
    }

    [Fact]
    public void Maker_from_vid_inside_a_device_path()
    {
        Assert.Equal("ASUS", ColorOrderAuto.DetectMaker("OpenRGB", "Controlador", @"\\?\hid#vid_0b05&pid_19af"));
        Assert.Null(ColorOrderAuto.DetectMaker("OpenRGB", "Controlador", "vid_ffff"));
    }
}

public class SearchIndexTests
{
    private static readonly SearchEntry[] Entries =
    {
        new("PageOptimize", "Ajustes e memória", "Otimizar", "otimiza ram"),
        new("PageOc", "Overclock", "Desempenho", "pbo curve optimizer"),
        new("PageComp", "Desligar aceleração do mouse", "Jogos · Competitivo"),
    };

    [Theory]
    [InlineData("memoria", "PageOptimize")]
    [InlineData("MEMÓRIA", "PageOptimize")]
    [InlineData("aceleracao", "PageComp")]
    [InlineData("curve optimizer", "PageOc")]
    [InlineData("competitivo", "PageComp")]
    public void Finds_entries_ignoring_accents_and_case(string query, string page)
    {
        Assert.Equal(page, SearchIndex.Search(query, Entries).First().Page);
    }

    [Fact]
    public void Label_prefix_beats_keyword_and_blank_finds_nothing()
    {
        var entries = new[] { new SearchEntry("A", "Outro", "x", "mouse"), new SearchEntry("B", "Mouse", "y") };
        Assert.Equal("B", SearchIndex.Search("mouse", entries)[0].Page);
        Assert.Empty(SearchIndex.Search("   ", entries));
    }

    [Fact]
    public void Results_are_capped()
    {
        var many = Enumerable.Range(0, 30).Select(i => new SearchEntry("P", $"Ajuste {i}", "x"));
        Assert.Equal(SearchIndex.MaxResults, SearchIndex.Search("ajuste", many).Count);
    }
}

public class AppLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thgtest-" + Guid.NewGuid().ToString("N"));

    public AppLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Writes_one_line_per_call_and_rotates_at_the_limit()
    {
        string path = Path.Combine(_dir, "t.log");
        AppLog.UseFile(path);
        AppLog.Info("teste", "linha\ncom quebra");
        Assert.Single(File.ReadAllLines(path));

        string big = new('x', 8192);
        for (int i = 0; i < 40; i++) AppLog.Info("teste", big);
        Assert.True(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path).Length <= AppLog.MaxBytes);
    }
}

public class MemoryTrimPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0);

    [Fact]
    public void Visible_window_never_trims() => Assert.False(MemoryTrimPolicy.ShouldTrim(Now, null, null, false));

    [Fact]
    public void Waits_until_hidden_long_enough()
    {
        Assert.False(MemoryTrimPolicy.ShouldTrim(Now, Now.AddSeconds(-29), null, false));
        Assert.True(MemoryTrimPolicy.ShouldTrim(Now, Now.AddSeconds(-30), null, false));
    }

    [Fact]
    public void Never_during_game_or_test() => Assert.False(MemoryTrimPolicy.ShouldTrim(Now, Now.AddHours(-1), null, true));

    [Fact]
    public void Respects_minimum_interval()
    {
        Assert.False(MemoryTrimPolicy.ShouldTrim(Now, Now.AddHours(-1), Now.AddMinutes(-9), false));
        Assert.True(MemoryTrimPolicy.ShouldTrim(Now, Now.AddHours(-1), Now.AddMinutes(-10), false));
    }

    [Fact]
    public void Snapshot_reads_this_process()
    {
        var m = MemorySnapshot.Read();
        Assert.True(m.WorkingSetMb > 0);
        Assert.Contains("MB", m.Summary);
    }
}

public class SettingsMigrationTests
{
    [Fact]
    public void Old_file_gets_the_automatic_order()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"OcAutoProfile\":0}")!;
        Assert.Equal(0, s.SettingsVersion);
        SettingsMigration.Apply(s);
        Assert.True(s.LightAutoOrder);
        Assert.Equal(0, s.OcAutoProfile);
        Assert.Equal(SettingsMigration.Current, s.SettingsVersion);
    }

    [Fact]
    public void Migration_runs_once_and_keeps_later_user_choice()
    {
        var s = new AppSettings();
        SettingsMigration.Apply(s);
        s.LightAutoOrder = false;
        s.OcRiskAccepted = true;
        SettingsMigration.Apply(s);
        Assert.False(s.LightAutoOrder);
        Assert.True(s.OcRiskAccepted);
    }

    [Fact]
    public void New_install_has_auto_order_and_no_whats_new()
    {
        var s = SettingsMigration.NewInstall("1.7.4");
        Assert.True(s.LightAutoOrder);
        Assert.False(WhatsNew.ShouldShow(s.LastSeenVersion, "1.7.4"));
    }

    [Theory]
    [InlineData(null, "1.7.4", true)]
    [InlineData("1.6", "1.7.4", true)]   // chave antiga, só Major.Minor
    [InlineData("1.7.1", "1.7.4", true)]
    [InlineData("1.7.4", "1.7.4", false)]
    [InlineData("1.7.4", "1.7.5", true)]
    [InlineData("1.7.5", "1.7.6", false)] // sem texto para a 1.7.6: nada a mostrar
    [InlineData("1.7.4", "1.8.0", false)]
    public void Whats_new_shows_once_per_version(string? lastSeen, string current, bool show)
    {
        Assert.Equal(show, WhatsNew.ShouldShow(lastSeen, current));
    }

    [Theory]
    [InlineData("v1.6.0", "", "1.6.0")]
    [InlineData("latest", "tHg Optimization 1.6.210", "1.6.210")]
    public void Updater_parses_release_version(string tag, string title, string expected)
    {
        Assert.Equal(Version.Parse(expected), AppUpdater.ParseVersion(tag, title));
    }
}
