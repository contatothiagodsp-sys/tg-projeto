using System.Collections;
using System.IO;
using System.Text.Json;

namespace ThgOptimization.Tests;

public class SettingsSnapshotTests
{
    [Fact]
    public void Every_collection_is_copied()
    {
        var s = new AppSettings { OverlayItems = new List<string> { "fps" } };
        var copy = s.Snapshot();
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (p.PropertyType == typeof(string) || p.PropertyType == typeof(byte[]) || !p.CanWrite) continue;
            if (!typeof(IEnumerable).IsAssignableFrom(p.PropertyType)) continue;
            Assert.False(ReferenceEquals(p.GetValue(s), p.GetValue(copy)), $"{p.Name} não entrou no Snapshot: a gravação ainda veria a lista viva.");
        }
    }

    [Fact]
    public void Nested_mutable_settings_are_copied_too()
    {
        var s = new AppSettings();
        s.GameProfiles["a"] = new GameProfile { Helpers = { new HelperProgram { Path = "x" } } };
        s.LcdScreens["l"] = new LcdScreenConfig { Brightness = 10 };
        s.Fans.Channels.Add(new FanChannelSettings { Id = "f" });
        var copy = s.Snapshot();
        s.GameProfiles["a"].Helpers.Clear();
        s.LcdScreens["l"].Brightness = 99;
        s.Fans.Channels[0].Id = "mudou";
        s.Fans.Channels.Clear();
        Assert.Single(copy.GameProfiles["a"].Helpers);
        Assert.Equal(10, copy.LcdScreens["l"].Brightness);
        Assert.Equal("f", copy.Fans.Channels[0].Id);
    }

    [Fact]
    public void Snapshot_keeps_the_values()
    {
        var s = new AppSettings { OcAutoProfile = 2, LightMaxFps = 15, HasVrr = true };
        s.TweakBackup["k"] = "v";
        s.BgCorePids.Add(7);
        var back = JsonSerializer.Deserialize<AppSettings>(s.SerializeSnapshot())!;
        Assert.Equal(2, back.OcAutoProfile);
        Assert.Equal(15, back.LightMaxFps);
        Assert.True(back.HasVrr);
        Assert.Equal("v", back.TweakBackup["k"]);
        Assert.Equal(new[] { 7 }, back.BgCorePids);
    }

    [Fact]
    public void Null_lists_from_an_old_file_do_not_break_the_save()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("""{"OverlayItems":null,"CustomGames":null,"TweakBackup":null,"LightColorOrder":null,"GameProfiles":null,"Fans":null,"LcdScreens":null}""")!;
        Assert.NotNull(JsonSerializer.Deserialize<AppSettings>(s.SerializeSnapshot()));
    }

    [Fact]
    public async Task Changing_lists_while_saving_never_produces_broken_json()
    {
        var s = new AppSettings();
        for (int i = 0; i < 200; i++) s.BenchRecords.Add(new BenchRecord(DateTime.Now, "gpu", i, "x"));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var writer = Task.Run(() =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
            {
                n++;
                int value = n;
                s.Mutate(() =>
                {
                    s.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, "gpu", value, "novo"));
                    if (s.BenchRecords.Count > 400) s.BenchRecords.RemoveRange(300, s.BenchRecords.Count - 300);
                    s.BgCorePids.Add(value);
                    if (s.BgCorePids.Count > 100) s.BgCorePids.Clear();
                });
                s.TweakBackup[$"k{n % 50}"] = "v"; // trava sozinho
                if (n % 50 == 0) s.TweakBackup.Clear();
            }
        });
        // Sem nenhum catch: com todo escritor na trava, a cópia nunca cruza com uma alteração nem lança.
        while (!stop.IsCancellationRequested)
            Assert.NotNull(JsonSerializer.Deserialize<AppSettings>(s.SerializeSnapshot()));
        await writer;
    }

    [Fact]
    public void Tweak_backup_keeps_its_json_format()
    {
        var s = new AppSettings();
        s.TweakBackup["a"] = "1";
        s.TweakBackup["b"] = null;
        string json = s.SerializeSnapshot();
        Assert.Contains("\"a\": \"1\"", json);
        var back = JsonSerializer.Deserialize<AppSettings>("""{"TweakBackup":{"a":"1","b":null}}""")!;
        Assert.Equal("1", back.TweakBackup["a"]);
        Assert.True(back.TweakBackup.ContainsKey("b"));
        Assert.Null(back.TweakBackup.GetValueOrDefault("nao-existe"));
    }

    [Fact]
    public void Tweak_backup_can_be_changed_while_enumerating()
    {
        var map = new SettingsMap { ["a"] = "1", ["b"] = "2" };
        foreach (var kv in map) map.Remove(kv.Key);
        Assert.Empty(map);
    }

    [Fact]
    public void Every_settings_list_write_outside_the_settings_goes_through_the_lock()
    {
        // A trava só protege se todo escritor a usar: uma alteração solta volta a poder derrubar o Save.
        string[] lists = { "BenchRuns", "BenchRecords", "StartupRemoved", "LightColorOrder", "GameProfiles", "CustomGames", "SuspendedPids", "BgCorePids", "ClientDemotedPids",
                           "StoppedServices", "Captures", "BenchLabSessions", "AbComparisons", "RemovedApps", "GameSessions", "OcProfiles", "LcdScreens" };
        var write = new System.Text.RegularExpressions.Regex($@"(?:_settings|_s|\bs|st)\.({string.Join("|", lists)})(?:\.(?:Add|AddRange|Insert|Remove|RemoveAll|RemoveAt|RemoveRange|Clear)\(|\[[^\]]+\]\s*=[^=])");
        foreach (string path in Directory.EnumerateFiles(RepoText.Root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || Path.GetFileName(path) is "AppSettings.cs" or "AppLog.cs") continue;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!write.IsMatch(lines[i])) continue;
                string around = string.Join("\n", lines[Math.Max(0, i - 4)..(i + 1)]);
                Assert.True(around.Contains(".Mutate("), $"{Path.GetFileName(path)}:{i + 1} altera uma lista das configurações fora de Mutate: {lines[i].Trim()}");
            }
        }
    }
}
