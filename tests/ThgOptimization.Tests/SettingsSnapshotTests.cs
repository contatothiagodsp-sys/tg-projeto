using System.Collections;
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
                s.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, "gpu", n, "novo"));
                if (s.BenchRecords.Count > 400) s.BenchRecords.RemoveRange(300, s.BenchRecords.Count - 300);
                s.BgCorePids.Add(n);
                if (s.BgCorePids.Count > 100) s.BgCorePids.Clear();
                s.TweakBackup[$"k{n % 50}"] = "v";
                if (n % 50 == 0) s.TweakBackup.Clear();
            }
        });
        int saved = 0;
        while (!stop.IsCancellationRequested)
        {
            string json;
            // Depois das tentativas, a corrida ainda pode lançar: não grava, e a próxima gravação pega o estado. O que nunca pode sair é JSON quebrado.
            try { json = s.SerializeSnapshot(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException) { continue; }
            Assert.NotNull(JsonSerializer.Deserialize<AppSettings>(json));
            saved++;
        }
        await writer;
        Assert.True(saved > 0);
    }
}
