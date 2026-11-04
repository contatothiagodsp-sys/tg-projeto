using System.IO;
using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Tests;

public sealed class SessionTimelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thg-sessoes-" + Guid.NewGuid().ToString("N"));
    private DateTime _clock = new(2026, 11, 3, 20, 0, 0);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SessionRecorder Recorder() => new(new SessionStore(_dir), () => _clock);

    private void AddSecond(SessionRecorder r, double fps = 100)
    {
        r.Add(1000 / fps, fps, 60, 40, 4500, 80, 65, 90, 2500, 200, 50, 40, 0, false, false);
        _clock = _clock.AddSeconds(1);
    }

    [Fact]
    public void Begin_sem_End_nao_grava_arquivo()
    {
        using var r = Recorder();
        r.Begin("CS2");
        AddSecond(r);
        Assert.True(r.IsRecording);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Dispose_solta_sem_gravar()
    {
        var r = Recorder();
        r.Begin("CS2");
        AddSecond(r);
        r.Dispose();
        Assert.False(r.IsRecording);
        Assert.Null(r.End());
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Sample_grava_uma_linha_por_segundo()
    {
        using var r = Recorder();
        r.Begin("CS2");
        for (int i = 0; i < 30; i++) AddSecond(r);
        var session = r.End(avgFps: 100, low1Fps: 70)!;
        Assert.Equal(31, File.ReadAllLines(session.FilePath!).Length); // cabeçalho + 30 amostras
        Assert.Equal(30, SessionStore.Load(session.FilePath!)!.Samples.Count);
        Assert.Equal(100, session.Header.AvgFps);
    }

    [Fact]
    public void Timestamp_e_relativo_ao_begin()
    {
        using var r = Recorder();
        r.Begin("CS2");
        AddSecond(r);
        AddSecond(r);
        var session = r.End()!;
        Assert.Equal(0, session.Samples[0].TimestampMs);
        Assert.Equal(1000, session.Samples[1].TimestampMs);
    }

    [Fact]
    public void Sessao_vazia_nao_grava()
    {
        using var r = Recorder();
        r.Begin("CS2");
        Assert.Null(r.End());
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Prune_mantem_apenas_50()
    {
        var store = new SessionStore(_dir);
        for (int i = 0; i < 53; i++)
        {
            var header = new SessionHeader(1, "CS2", new DateTime(2026, 1, 1).AddHours(i), 60, 100, 70, 0);
            store.Save(new SessionTimeline(header, new[] { Sample(0) }), new[] { 10f });
        }
        var left = store.Files().ToList();
        Assert.Equal(50, left.Count);
        Assert.Equal(50, Directory.EnumerateFiles(_dir, "*.frames.gz").Count());
        Assert.DoesNotContain(left, f => Path.GetFileName(f).StartsWith("20260101-000000", StringComparison.Ordinal)); // foi a mais antiga que saiu
    }

    private static SessionSample Sample(long ms, double? frametime = 10, int stutters = 0) =>
        new(ms, frametime, 100, 60, 40, 4500, 80, 65, 90, 2500, 200, 50, 40, null, false, stutters, false);

    [Theory]
    [InlineData("isto nao e json")]
    [InlineData("{\"Version\":1,\"Game\":\"x\",\"Start\":\"2026-01-01T00:00:00\",\"DurationSeconds\":1,\"AvgFps\":1,\"Low1Fps\":1,\"Stutters\":0}\n{corrompido")]
    [InlineData("")]
    public void Load_de_jsonl_corrompido_devolve_nulo(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "ruim.jsonl");
        File.WriteAllText(path, content);
        Assert.Null(SessionStore.Load(path));
    }

    [Fact]
    public void Arquivo_ruim_nao_esconde_as_outras_sessoes()
    {
        var store = new SessionStore(_dir);
        store.Save(new SessionTimeline(new SessionHeader(1, "CS2", new DateTime(2026, 1, 1), 60, 100, 70, 0), new[] { Sample(0) }), null);
        File.WriteAllText(Path.Combine(_dir, "ruim.jsonl"), "lixo");
        Assert.Equal("CS2", Assert.Single(store.List()).Header.Game);
    }

    [Fact]
    public void Export_csv_tem_cabecalho_present_mon()
    {
        var session = new SessionTimeline(new SessionHeader(1, "CS2", DateTime.Now, 2, 100, 70, 0), new[] { Sample(0, 12.5), Sample(1000, null) });
        var lines = session.ToPresentMonCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Application,TimeInSeconds,msBetweenPresents", lines[0]);
        Assert.Equal("CS2,0.000,12.500", lines[1]);
        Assert.Equal(2, lines.Length); // segundo sem leitura não vira linha com zero
    }

    [Fact]
    public void Export_usa_cada_frame_quando_ha_gravacao()
    {
        var session = new SessionTimeline(new SessionHeader(1, "x", DateTime.Now, 1, 100, 70, 0), new[] { Sample(0) }, new[] { 10f, 20f });
        var lines = session.ToPresentMonCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "x,0.010,10.000", "x,0.030,20.000" }, lines[1..]);
    }

    [Theory]
    [InlineData("=CMD()", "'=CMD()")]
    [InlineData("Jogo, O \"Bom\"", "Jogo O Bom")]
    public void Nome_do_jogo_nao_quebra_o_csv_nem_vira_formula(string name, string expected)
    {
        Assert.Equal(expected, SessionTimeline.CsvSafe(name));
    }

    [Theory]
    [InlineData("Counter-Strike 2", "counter-strike-2")]
    [InlineData(@"..\..\Windows", "windows")]
    [InlineData("Pokémon: Edição", "pokemon-edicao")]
    [InlineData("???", "jogo")]
    public void Nome_do_arquivo_nao_sai_da_pasta(string game, string slug)
    {
        Assert.Equal(slug, SessionStore.Slug(game));
    }

    [Fact]
    public void Frames_salvos_voltam_iguais_e_marcam_o_pior_de_cada_segundo()
    {
        using var r = Recorder();
        r.Begin("CS2");
        AddSecond(r);
        AddSecond(r);
        // Frames de 10 ms e um de 200 ms no segundo 0.
        var instants = new List<double> { 0 };
        for (int i = 0; i < 90; i++) instants.Add(instants[^1] + 10);
        instants.Add(instants[^1] + 200);
        for (int i = 0; i < 90; i++) instants.Add(instants[^1] + 10);

        var session = r.End(instants, avgFps: 90)!;

        var loaded = SessionStore.Load(session.FilePath!)!;
        Assert.Equal(instants.Count - 1, loaded.FramesMs.Count);
        Assert.Equal(200, loaded.Samples[1].FrametimeMs); // o frame de 200 ms termina depois de 1 s
        Assert.Equal(1, loaded.Samples[1].StuttersThisSecond);
        Assert.Equal(0, loaded.Samples[0].StuttersThisSecond);
        Assert.Equal(1, loaded.Header.Stutters);
        var worst = loaded.WorstFrames(1)[0];
        Assert.Equal(200, worst.FrameMs);
        Assert.InRange(worst.AtSeconds, 0.8, 1.0);
    }

    [Fact]
    public void Percentis_saem_dos_frames()
    {
        var frames = Enumerable.Range(1, 1000).Select(i => (float)i).ToArray();
        var p = new SessionTimeline(new SessionHeader(1, "x", DateTime.Now, 1, 1, 1, 0), new[] { Sample(0) }, frames).Percentiles();
        Assert.Equal(500, p.P50);
        Assert.Equal(990, p.P99);
        Assert.Equal(999, p.P999);
    }

    [Fact]
    public void Amostra_mais_proxima_sem_passar_do_fim()
    {
        var session = new SessionTimeline(new SessionHeader(1, "x", DateTime.Now, 3, 1, 1, 0), new[] { Sample(0), Sample(1000), Sample(2000) });
        Assert.Equal(1000, session.SampleAt(1500)!.TimestampMs);
        Assert.Equal(2000, session.SampleAt(9999)!.TimestampMs);
        Assert.Equal(0, session.SampleAt(-5)!.TimestampMs);
    }

    [Fact]
    public void Zoom_mantem_o_ponto_do_mouse_e_nao_sai_da_sessao()
    {
        var v = new TimelineViewport(100_000);
        v.Zoom(0.5, 0.5);
        Assert.Equal((25_000, 75_000), (v.StartMs, v.EndMs));
        v.Pan(-10);
        Assert.Equal(0, v.StartMs);
        v.Pan(10);
        Assert.Equal(100_000, v.EndMs);
        for (int i = 0; i < 40; i++) v.Zoom(0.5, 0.5);
        Assert.Equal(TimelineViewport.MinSpanMs, v.SpanMs);
        v.Reset();
        Assert.Equal(100_000, v.SpanMs);
        v.Zoom(0.1, 99);
        Assert.Equal(100_000, v.SpanMs);
    }
}
