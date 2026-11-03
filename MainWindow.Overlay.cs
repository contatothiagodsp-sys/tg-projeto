using System.Windows;
using System.Windows.Controls;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization;

/// <summary>Overlay: o que mostrar (escolhido na aba Overlay) e a montagem das linhas de sensores.</summary>
public partial class MainWindow
{
    private HashSet<string>? _ovShow;
    private HashSet<string> OvShow => _ovShow ??= _settings.OverlayItems is { } l ? l.ToHashSet() : OverlayMetrics.Defaults();
    private HashSet<string>? _ovEffective;
    /// <summary>O que foi escolhido, filtrado pelo perfil. Só muda quando o usuário mexe.</summary>
    private HashSet<string> OvEffective => _ovEffective ??= MonitorProfiles.Effective(OvShow, Profile);
    private MonitorProfile Profile => MonitorProfiles.Parse(_settings.MonitorProfile);
    private long _ovEventsBefore;
    private long _ovEventsAt;

    private void SyncOverlayProfile()
    {
        var p = Profile;
        OvProfileEco.IsChecked = p == MonitorProfile.Economy;
        OvProfileBal.IsChecked = p == MonitorProfile.Balanced;
        OvProfileDet.IsChecked = p == MonitorProfile.Detailed;
    }

    private void OvProfile_Checked(object sender, RoutedEventArgs e)
    {
        if (!_overlayReady || sender is not FrameworkElement { Tag: string key } || key == _settings.MonitorProfile) return;
        _settings.MonitorProfile = key;
        _settings.Save();
        _ovEffective = null;
        if (_overlayTimer != null) _overlayTimer.Interval = MonitorProfiles.Interval(Profile);
        UpdateOverlay();
    }

    /// <summary>Cria as caixas de seleção da aba Overlay, agrupadas por componente.</summary>
    private void BuildOverlayPicker()
    {
        OvItemsPanel.Children.Clear();
        foreach (var group in OverlayMetrics.All.GroupBy(m => m.Group))
        {
            OvItemsPanel.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(), Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4),
            });
            var wrap = new WrapPanel();
            foreach (var m in group)
            {
                var cb = new CheckBox { Content = m.Label, Tag = m.Key, IsChecked = OvShow.Contains(m.Key), Margin = new Thickness(0, 0, 18, 6) };
                cb.Click += (_, _) =>
                {
                    if (cb.IsChecked == true) OvShow.Add(m.Key); else OvShow.Remove(m.Key);
                    SaveOverlayItems();
                };
                wrap.Children.Add(cb);
            }
            OvItemsPanel.Children.Add(wrap);
        }
    }

    private void SaveOverlayItems()
    {
        _settings.OverlayItems = OverlayMetrics.All.Select(m => m.Key).Where(OvShow.Contains).ToList();
        _ovEffective = null;
        _settings.Save();
        UpdateOverlay();
    }

    private void OvPreset_Click(object sender, RoutedEventArgs e)
    {
        var keys = (string)((FrameworkElement)sender).Tag switch
        {
            "all" => OverlayMetrics.All.Select(m => m.Key).ToHashSet(),
            "min" => new HashSet<string> { "fps", "frametime", "low1" },
            _ => OverlayMetrics.Defaults(),
        };
        _ovShow = keys;
        SaveOverlayItems();
        BuildOverlayPicker();
    }

    // ------------------------------------------------------------------ leitura dos sensores

    private SensorRow? GpuRow(SensorType type, string name, bool startsWith = false) =>
        _gpus.Count == 0 ? null : _monitor.Rows.FirstOrDefault(r => r.Hardware == _gpus[0].Name && r.Type == type &&
                                                                   (startsWith ? r.Name.StartsWith(name) : r.Name == name) && r.RawValue != null);

    private uint? MainGpuClock(int nvmlType, string lhmName)
    {
        if (_gpus.Count > 0 && _gpus[0].NvmlHandle != IntPtr.Zero && Nvml.Clock(_gpus[0].NvmlHandle, nvmlType) is uint c) return c;
        return GpuRow(SensorType.Clock, lhmName)?.RawValue is float v && v > 0 ? (uint)v : null;
    }

    /// <summary>VRAM usada e total em GB: NVML quando disponível, senão LibreHardwareMonitor (MB).</summary>
    private (double Used, double Total)? MainGpuVram()
    {
        if (_gpus.Count == 0) return null;
        if (_gpus[0].NvmlHandle != IntPtr.Zero && Nvml.MemoryInfo(_gpus[0].NvmlHandle) is Nvml.Memory m && m.Total > 0)
            return (m.Used / 1073741824.0, m.Total / 1073741824.0);
        if (GpuRow(SensorType.SmallData, "GPU Memory Used")?.RawValue is float u && GpuRow(SensorType.SmallData, "GPU Memory Total")?.RawValue is float t && t > 0)
            return (u / 1024.0, t / 1024.0);
        return null;
    }

    private (float? Avg, float? Max) CpuClocks()
    {
        var rows = _monitor.Rows;
        double sum = 0;
        float max = 0;
        int count = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.HardwareType != HardwareType.Cpu || r.Type != SensorType.Clock || r.RawValue is not float v || v <= 0) continue;
            if (!r.Name.StartsWith("Core #", StringComparison.Ordinal) || r.Name.Contains("Effective")) continue;
            sum += v;
            if (v > max) max = v;
            count++;
        }
        return count > 0 ? ((float)(sum / count), max) : (null, null);
    }

    private string EtwStateText()
    {
        long events = _frames.EventsReceived, now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = _ovEventsAt != 0 ? System.Diagnostics.Stopwatch.GetElapsedTime(_ovEventsAt, now).TotalSeconds : 0;
        double rate = seconds > 0 ? (events - _ovEventsBefore) / seconds : 0;
        _ovEventsBefore = events;
        _ovEventsAt = now;
        return _frames.State switch
        {
            FrameCollectionState.Running => $"{rate:0} eventos/s" + (_frames.EventsLost > 0 ? $", {_frames.EventsLost} perdidos" : ""),
            FrameCollectionState.Failed => "falhou",
            _ => "desligado",
        };
    }

    private readonly List<OverlayLine> _ovLineCache = new();

    /// <summary>Monta as linhas GPU / VRAM / CPU / RAM só com o que foi escolhido; linha vazia não aparece.</summary>
    private List<OverlayLine> BuildOverlayLines(FrameStats? f)
    {
        var show = OvEffective;
        // A amostra percorre os sensores e consulta o driver de vídeo: só se algum item escolhido depende dela.
        var s = OverlayMetrics.NeedsLoadSample(show) ? BottleneckAnalyzer.Sample(_monitor, _gpus.FirstOrDefault(), f) : null;
        // Listas reaproveitadas a cada quadro: OverlayWindow.Update copia os textos antes da próxima chamada.
        var lines = _ovLineCache;
        lines.Clear();
        string T(float? v) => v is float x ? $"{x:0} °C" : "—";
        string P(double? v) => v is double x ? $"{x:0}%" : "—";
        void Line(string label, List<string> parts) { if (parts.Count > 0) lines.Add(new OverlayLine(label, string.Join("  ", parts))); }
        // Uma lista por linha, de propósito: reaproveitar uma só funcionava enquanto ninguém pedisse a próxima antes de terminar a anterior.
        static List<string> Parts() => new(4);

        var frame = Parts();
        var d = f?.Detail;
        if (show.Contains("low01")) frame.Add(d != null ? $"0,1% {d.Low01PercentileFps:0}" : "0,1% —");
        if (show.Contains("p99")) frame.Add(d != null ? $"P99 {d.P99Ms:0.0} ms" : "P99 —");
        if (show.Contains("p999")) frame.Add(d != null ? $"P99,9 {d.P999Ms:0.0} ms" : "P99,9 —");
        if (show.Contains("stutters_min")) frame.Add(d != null ? $"{d.StuttersPerMinute:0} engasgos/min" : "— engasgos/min");
        Line("Frame", frame);

        var gpu = Parts();
        if (show.Contains("gpu_load")) gpu.Add(P(s?.GpuLoad));
        if (show.Contains("gpu_temp")) gpu.Add(T(s?.GpuTemp));
        if (show.Contains("gpu_hotspot")) gpu.Add("HS " + T(GpuRow(SensorType.Temperature, "GPU Hot Spot")?.RawValue));
        if (show.Contains("gpu_clock")) gpu.Add(MainGpuClock(Nvml.ClockGraphics, "GPU Core") is uint c ? $"{c} MHz" : "— MHz");
        if (show.Contains("gpu_memclock")) gpu.Add(MainGpuClock(Nvml.ClockMem, "GPU Memory") is uint mc ? $"mem {mc} MHz" : "mem —");
        if (show.Contains("gpu_power")) gpu.Add(MainGpuPower() is float w ? $"{w:0} W" : "— W");
        if (show.Contains("gpu_volt")) gpu.Add(GpuRow(SensorType.Voltage, "GPU Core")?.RawValue is float v ? $"{v:0.000} V" : "— V");
        if (show.Contains("gpu_fan"))
            gpu.Add(GpuRow(SensorType.Fan, "GPU Fan", true)?.RawValue is float rpm && rpm > 0 ? $"{rpm:0} RPM"
                  : GpuRow(SensorType.Control, "GPU Fan", true)?.RawValue is float pct ? $"fan {pct:0}%" : "fan —");
        if (show.Contains("gpu_perfcap") && MainGpuPerfCap() is { Length: > 0 } cap) gpu.Add(cap);
        Line("GPU", gpu);

        var vram = Parts();
        var vr = OverlayMetrics.NeedsVram(show) ? MainGpuVram() : null;
        if (show.Contains("vram_used")) vram.Add(vr is { } a ? $"{a.Used:0.0} GB" : "—");
        if (show.Contains("vram_pct")) vram.Add(P(s?.VramPct));
        if (show.Contains("vram_temp")) vram.Add(T(GpuRow(SensorType.Temperature, "GPU Memory")?.RawValue));
        Line("VRAM", vram);

        var cpu = Parts();
        // CpuClocks percorre todos os sensores: só se algum clock foi escolhido.
        var (avg, max) = show.Contains("cpu_clock") || show.Contains("cpu_clockmax") ? CpuClocks() : (null, null);
        if (show.Contains("cpu_load")) cpu.Add(P(s?.CpuTotal));
        if (show.Contains("cpu_maxcore")) cpu.Add(s?.CpuMaxCore is float mx ? $"núcleo {mx:0}%" : "núcleo —");
        if (show.Contains("cpu_temp")) cpu.Add(T(s?.CpuTemp));
        if (show.Contains("cpu_clock")) cpu.Add(avg is float ca ? $"{ca / 1000:0.00} GHz" : "— GHz");
        if (show.Contains("cpu_clockmax")) cpu.Add(max is float cm ? $"pico {cm / 1000:0.00} GHz" : "pico —");
        if (show.Contains("cpu_power")) cpu.Add(_monitor.CpuPower is float cw && cw > 0 ? $"{cw:0} W" : "— W"); // 0 = sem leitura (precisa de administrador)
        Line("CPU", cpu);

        var ram = Parts();
        var (total, availB) = OverlayMetrics.NeedsMemoryStatus(show) ? Optimizer.MemoryStatus() : (0, 0);
        if (show.Contains("ram_used")) ram.Add(total > 0 ? $"{(total - availB) / 1073741824.0:0.0} GB" : "—");
        if (show.Contains("ram_pct")) ram.Add(P(s?.RamPct));
        Line("RAM", ram);

        if (show.Contains("mem_pressure") && total > 0)
            lines.Add(new OverlayLine("Mem", "pressão " + Infrastructure.MemoryDiagnostics.LevelText(Infrastructure.MemoryDiagnostics.Classify(availB * 100.0 / total, 0))));
        if (show.Contains("present_mode")) lines.Add(new OverlayLine("Modo", _lastPresentation?.ModeText ?? "sem coleta (Configurações)"));
        if (show.Contains("etw_state")) lines.Add(new OverlayLine("ETW", EtwStateText()));
        return lines;
    }
}
