using LibreHardwareMonitor.Hardware;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

/// <summary>As leituras em uma passada só precisam escolher as mesmas linhas das consultas LINQ que substituíram.</summary>
public class SensorHotPathTests
{
    private static SensorRow Row(string hardware, HardwareType kind, SensorType type, string name, float? value)
    {
        var row = new SensorRow { Hardware = hardware, HardwareType = kind, Type = type, Name = name };
        row.Update(value, null, null);
        return row;
    }

    private static SensorRow Cpu(SensorType type, string name, float? value) => Row("CPU", HardwareType.Cpu, type, name, value);
    private static SensorRow Gpu(SensorType type, string name, float? value) => Row("GPU", HardwareType.GpuNvidia, type, name, value);

    private static SensorMonitor Monitor(params SensorRow[] rows)
    {
        var monitor = new SensorMonitor();
        foreach (var row in rows) monitor.Rows.Add(row);
        return monitor;
    }

    [Fact]
    public void Sample_picks_the_same_rows()
    {
        using var m = Monitor(
            Gpu(SensorType.Temperature, "GPU Hot Spot", 90),
            Gpu(SensorType.Temperature, "GPU Core", 70),
            Gpu(SensorType.Load, "GPU Memory", 11),
            Gpu(SensorType.Load, "GPU Core", 55),
            Gpu(SensorType.SmallData, "GPU Memory Used", 4),
            Gpu(SensorType.SmallData, "GPU Memory Total", 8),
            Row("Outra", HardwareType.GpuAmd, SensorType.Load, "GPU Core", 99),
            Cpu(SensorType.Load, "CPU Total", 30),
            Cpu(SensorType.Load, "CPU Core #1", 40),
            Cpu(SensorType.Load, "CPU Core #2 Thread #1", 100),
            Cpu(SensorType.Load, "CPU Core #3", 80),
            Cpu(SensorType.Load, "CPU Core #4", 80),
            Cpu(SensorType.Load, "CPU Core #5", null),
            Row("RAM", HardwareType.Memory, SensorType.Load, "Virtual Memory", 5),
            Row("RAM", HardwareType.Memory, SensorType.Load, "Memory", 65));

        var s = BottleneckAnalyzer.Sample(m, new GpuInfo { Name = "GPU" }, null);

        Assert.Equal(55, s.GpuLoad);
        Assert.Equal(70, s.GpuTemp);
        Assert.Equal(50, s.VramPct);
        Assert.Equal(80, s.CpuMaxCore);
        Assert.Equal("núcleo 3", s.BusiestCore); // empate: fica o primeiro, como no OrderByDescending
        Assert.Equal(65, s.RamPct);
        Assert.False(s.GpuThermalCap);
    }

    [Fact]
    public void Sample_without_gpu_or_named_core_temperature()
    {
        using var m = Monitor(Gpu(SensorType.Temperature, "GPU Hot Spot", 90), Gpu(SensorType.Load, "GPU Core", 55));
        Assert.Equal(90, BottleneckAnalyzer.Sample(m, new GpuInfo { Name = "GPU" }, null).GpuTemp);
        var none = BottleneckAnalyzer.Sample(m, null, null);
        Assert.Null(none.GpuLoad);
        Assert.Null(none.GpuTemp);
        Assert.Null(none.VramPct);
    }

    [Fact]
    public void Live_keeps_the_order_and_the_first_matching_rows()
    {
        using var m = Monitor(
            Cpu(SensorType.Temperature, "CCD1 (Tdie)", 60),
            Cpu(SensorType.Temperature, "CCD2 (Tdie)", 0),
            Cpu(SensorType.Temperature, "CCD3 (Tdie)", 61),
            Row("Placa", HardwareType.SuperIO, SensorType.Voltage, "Vcore", 1.2f),
            Cpu(SensorType.Voltage, "Core #1 VID", 1.1f),
            Cpu(SensorType.Voltage, "Core VID", 1.3f),
            Cpu(SensorType.Voltage, "Core (SVI3 TFN)", 1.25f),
            Cpu(SensorType.Clock, "Bus Speed", 100));

        var rows = CpuInfo.Live(m);

        Assert.Equal(new[] { "Temperatura (pacote / Tctl)", "Temperatura CCD1 (Tdie)", "Temperatura CCD3 (Tdie)", "Consumo do pacote",
                             "Core VID", "Tensão do núcleo (Vcore)", "Carga total", "Clock do barramento" },
                     rows.Select(r => r.Key));
        Assert.Equal(m.Rows[5].Value, rows[4].Value);
        Assert.Equal(m.Rows[6].Value, rows[5].Value);
    }

    [Fact]
    public void Live_falls_back_to_the_highest_core_vid_and_to_the_board_vcore()
    {
        using var m = Monitor(
            Cpu(SensorType.Voltage, "Core #1 VID", 1.1f),
            Cpu(SensorType.Voltage, "Core #2 VID", 1.4f),
            Cpu(SensorType.Voltage, "Core #3 VID", 1.2f),
            Row("Placa", HardwareType.SuperIO, SensorType.Voltage, "CPU Vcore", 1.2f));

        var rows = CpuInfo.Live(m).ToDictionary(r => r.Key, r => r.Value);

        Assert.Equal(m.Rows[1].Value, rows["Core VID (maior entre os núcleos)"]);
        Assert.Equal(m.Rows[3].Value, rows["Tensão do núcleo (Vcore)"]);
        Assert.False(rows.ContainsKey("Clock do barramento"));
    }

    [Fact]
    public void Cores_group_clock_temperature_and_thread_load_by_core()
    {
        using var m = Monitor(
            Row("Placa", HardwareType.Motherboard, SensorType.Clock, "Core #9", 1),
            Cpu(SensorType.Clock, "Core #2", 4000),
            Cpu(SensorType.Clock, "Core #1", 5000),
            Cpu(SensorType.Temperature, "Core #1", 50),
            Cpu(SensorType.Load, "CPU Core #1 Thread #2", 20),
            Cpu(SensorType.Load, "CPU Core #1 Thread #1", 10),
            Cpu(SensorType.Load, "CPU Core #2", 30));

        var cores = CpuInfo.Cores(m);

        Assert.Equal(new[] { "Núcleo 1", "Núcleo 2" }, cores.Select(c => c.Core));
        Assert.Equal("T1 10% · T2 20%", cores[0].Load);
        Assert.Equal(m.Rows[3].Value, cores[0].Temp);
        Assert.Equal("30%", cores[1].Load);
        Assert.Equal(CpuInfo.Dash, cores[1].Temp);
    }
}

public class SensorRefreshGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Second_request_waits_for_the_running_read_without_reading_again()
    {
        var gate = new SensorRefreshGate();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int polls = 0, applied = 0;
        Task<int> Poll() { polls++; return release.Task; }

        var first = gate.RunAsync(light: false, Poll, _ => applied++);
        var second = gate.RunAsync(light: false, Poll, _ => applied++);
        Assert.False(second.IsCompleted);
        release.SetResult(1);
        await Task.WhenAll(first, second).WaitAsync(Patience);

        Assert.Equal(1, polls);
        Assert.Equal(1, applied); // e já aplicada: quem esperou lê a lista em dia
    }

    [Fact]
    public async Task Full_request_during_a_light_read_reads_again_after_it()
    {
        var gate = new SensorRefreshGate();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<string>();
        void Apply(string r) { lock (applied) applied.Add(r); }

        var light = gate.RunAsync(light: true, () => release.Task, Apply);
        var full = gate.RunAsync(light: false, () => Task.FromResult("completa"), Apply);
        Assert.False(full.IsCompleted);
        release.SetResult("leve");
        await Task.WhenAll(light, full).WaitAsync(Patience);

        Assert.Equal(new[] { "leve", "completa" }, applied);
    }

    [Fact]
    public async Task Light_request_during_a_full_read_needs_no_read_of_its_own()
    {
        var gate = new SensorRefreshGate();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int lightPolls = 0;

        var full = gate.RunAsync(light: false, () => release.Task, _ => { });
        var light = gate.RunAsync(light: true, () => { lightPolls++; return Task.FromResult(1); }, _ => { });
        release.SetResult(1);
        await Task.WhenAll(full, light).WaitAsync(Patience);

        Assert.Equal(0, lightPolls);
    }

    [Fact]
    public async Task Failed_read_reaches_who_asked_and_does_not_block_the_next()
    {
        var gate = new SensorRefreshGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync<int>(light: false, () => Task.FromException<int>(new InvalidOperationException("hardware")), _ => { }));

        int applied = 0;
        await gate.RunAsync(light: false, () => Task.FromResult(1), v => applied = v).WaitAsync(Patience);
        Assert.Equal(1, applied);
    }
}
