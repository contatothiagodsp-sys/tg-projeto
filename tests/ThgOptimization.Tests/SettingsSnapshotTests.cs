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
    public async Task Reading_copies_while_another_thread_changes_the_lists_never_throws()
    {
        var s = new AppSettings();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = stop.Token;
        var writer = Task.Run(() =>
        {
            int n = 0;
            while (!token.IsCancellationRequested)
            {
                n++;
                int value = n;
                s.Mutate(() =>
                {
                    s.GameSessions.Insert(0, new GameSessionRecord(DateTime.Now, "jogo", 1, value, 1, false));
                    if (s.GameSessions.Count > 300) s.GameSessions.RemoveRange(200, s.GameSessions.Count - 200);
                    s.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, "gpu", value, "x"));
                    if (s.BenchRecords.Count > 300) s.BenchRecords.Clear();
                    s.BgCorePids.Add(value);
                    if (s.BgCorePids.Count > 300) s.BgCorePids.Clear();
                    s.StoppedServices.Add("SysMain");
                    if (s.StoppedServices.Count > 300) s.StoppedServices.Clear();
                });
            }
        });
        // Sem nenhum catch: com o selector inteiro sob a trava, a enumeração nunca cruza com uma alteração.
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            long total = 0;
            while (!token.IsCancellationRequested)
            {
                total += s.CopyList(x => x.GameSessions).Count;
                total += s.CopyList(x => x.BenchRecords.Where(r => r.Test == "gpu")).Count;
                total += s.CopyList(x => x.BgCorePids).Sum();
                total += s.Read(x => x.StoppedServices.Count(n => n == "SysMain"));
            }
            return total;
        })).ToList();
        await writer;
        await Task.WhenAll(readers);
        Assert.All(readers, r => Assert.True(r.Result > 0));
    }

    [Fact]
    public void Copied_list_does_not_follow_the_original()
    {
        var s = new AppSettings();
        s.BgCorePids.Add(1);
        var copy = s.CopyList(x => x.BgCorePids);
        s.Mutate(() => s.BgCorePids.Add(2));
        Assert.Equal(new[] { 1 }, copy);
    }

    /// <summary>Com cada coleção do arquivo em null, lidas por reflexão: uma lista nova entra sozinha na conferência.</summary>
    [Fact]
    public void Migration_replaces_every_null_collection_from_the_file()
    {
        var names = typeof(AppSettings).GetProperties()
            .Where(p => p.CanWrite && p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name).Append(nameof(AppSettings.Fans)).ToList();
        string json = "{" + string.Join(",", names.Select(n => $"\"{n}\":null")) + "}";
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Null(s.GameSessions); // o desserializador realmente deixa null: é o caso que a migração cobre

        Infrastructure.SettingsMigration.Apply(s, logicalProcessors: 16);

        // Nestes, null significa alguma coisa: "itens padrão" e "sem cópia".
        foreach (string name in names.Except(new[] { nameof(AppSettings.OverlayItems), nameof(AppSettings.ComDbBackup) }))
            Assert.NotNull(typeof(AppSettings).GetProperty(name)!.GetValue(s));
        Assert.Null(s.OverlayItems);
        Assert.Null(s.ComDbBackup);
        Assert.Equal(5, s.OcProfiles.Count);
        Assert.NotNull(JsonSerializer.Deserialize<AppSettings>(s.SerializeSnapshot()));
    }

    [Fact]
    public void Migration_removes_null_items_of_dictionaries_and_null_nested_lists()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("""
            {"GameProfiles":{"a":null,"b":{"Helpers":null}},"LcdScreens":{"x":null},"Fans":{"Channels":null}}
            """)!;

        Infrastructure.SettingsMigration.Apply(s, logicalProcessors: 16);

        Assert.Equal(new[] { "b" }, s.GameProfiles.Keys);
        Assert.Empty(s.GameProfiles["b"].Helpers);
        Assert.Empty(s.LcdScreens);
        Assert.Empty(s.Fans.Channels);
    }

    [Fact]
    public void Legacy_sanitize_accepts_a_file_with_null_collections()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("""{"GameProfiles":null,"StartupRemoved":null,"TweakBackup":null}""")!;
        LegacySettingsMigration.Sanitize(s);
        Assert.Empty(s.GameProfiles);
        Assert.Empty(s.StartupRemoved);
        Assert.Empty(s.TweakBackup);
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
