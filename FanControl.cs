using System.ComponentModel;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization;

/// <summary>
/// Uma saída de fan que o tHg consegue comandar: cabo de 4 pinos da placa-mãe (LibreHardwareMonitor) ou porta da
/// controladora Lian Li UNI FAN TL.
/// </summary>
public sealed class FanOutput
{
    /// <summary>Saída da placa-mãe.</summary>
    public FanOutput(string id, string chip, string name, IControl control, ISensor controlSensor, ISensor? rpm)
        : this(id, chip, name, v => control.SetSoftware(v), control.SetDefault,
               () => rpm?.Value is float r && r > 0 ? $"{r:0} RPM" : "sem leitura de RPM",
               control.MinSoftwareValue, control.MaxSoftwareValue) { }

    public FanOutput(string id, string chip, string name, Action<float> set, Action setDefault, Func<string> rpm, float min = 0, float max = 100)
    { Id = id; Chip = chip; Name = name; Set = set; SetDefault = setDefault; RpmText = rpm; Min = min; Max = max; }

    public string Id { get; }
    public string Chip { get; }
    public string Name { get; }
    public Action<float> Set { get; }
    public Action SetDefault { get; }
    public Func<string> RpmText { get; }
    public float Min { get; }
    public float Max { get; }
}

/// <summary>Configuração de uma saída (fica nas configurações).</summary>
public sealed class FanChannelSettings
{
    public string Id { get; set; } = "";
    /// <summary>bios | silent | balanced | turbo | custom</summary>
    public string Preset { get; set; } = "bios";
    /// <summary>cpu | gpu | max</summary>
    public string Source { get; set; } = "cpu";
    /// <summary>Curva personalizada: "temperatura:porcentagem" separados por vírgula.</summary>
    public string Custom { get; set; } = "30:25, 50:35, 65:55, 75:80, 85:100";
}

public sealed class FanSettings
{
    public List<FanChannelSettings> Channels { get; set; } = new();
    /// <summary>Quantos °C a temperatura precisa cair abaixo do último pico antes de a fan desacelerar.</summary>
    public double HysteresisC { get; set; } = 3;
    /// <summary>Constante de tempo (s) da média móvel exponencial (EMA) da temperatura que a curva usa.</summary>
    public double EmaSeconds { get; set; } = 3;
    /// <summary>Segundos que a temperatura precisa ficar baixa antes de começar a desacelerar.</summary>
    public double FallDelayS { get; set; } = 8;
    /// <summary>Velocidade máxima de subida e de descida (% por segundo).</summary>
    public double RiseRate { get; set; } = 15;
    public double FallRate { get; set; } = 3;
}

/// <summary>Estado ao vivo de uma saída, para a tela.</summary>
public sealed class FanChannelView : INotifyPropertyChanged
{
    public FanOutput Output { get; }
    public FanChannelSettings Config { get; }
    public FanChannelView(FanOutput o, FanChannelSettings c) { Output = o; Config = c; }
    public string Title => $"{Output.Name} · {Output.Chip}";
    private string _live = "";
    public string Live { get => _live; set { _live = value; PropertyChanged?.Invoke(this, new(nameof(Live))); } }
    public string Preset { get => Config.Preset; set { Config.Preset = value; PropertyChanged?.Invoke(this, new(nameof(Preset))); } }
    public string Source { get => Config.Source; set { Config.Source = value; PropertyChanged?.Invoke(this, new(nameof(Source))); } }
    public string Custom { get => Config.Custom; set { Config.Custom = value; PropertyChanged?.Invoke(this, new(nameof(Custom))); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    // Estado do controlador (histerese)
    internal double Duty = -1, SmoothT = double.NaN, SmoothCpu = double.NaN, PeakT, LowFor;
}

/// <summary>
/// Curvas de fan com histerese, no estilo do FanControl: a temperatura é suavizada, a fan sobe rápido (com limite de
/// % por segundo) e só desce depois de a temperatura ficar abaixo do último pico menos a histerese por alguns segundos,
/// e mesmo assim devagar. Assim um pico de 1 s da CPU não faz as fans acelerarem e pararem.
/// Segurança: sem leitura de temperatura ou com a CPU acima de 90 °C, 100%; ao fechar o app, o controle volta à BIOS.
/// </summary>
public static class FanCurves
{
    public static readonly (string Key, string Label)[] Presets =
    {
        ("bios", "Da BIOS (tHg não mexe)"), ("silent", "Silencioso"), ("balanced", "Equilibrado"), ("turbo", "Turbo"), ("custom", "Personalizado"),
    };

    public static readonly (string Key, string Label)[] Sources = { ("cpu", "CPU"), ("gpu", "Placa de vídeo"), ("max", "A mais quente") };

    public static List<(double T, double P)> Curve(FanChannelSettings c) => c.Preset switch
    {
        "silent" => new() { (30, 20), (55, 30), (70, 50), (80, 75), (88, 100) },
        "balanced" => new() { (30, 30), (50, 40), (65, 60), (75, 80), (85, 100) },
        "turbo" => new() { (30, 45), (45, 60), (60, 80), (70, 100) },
        _ => ParseCustom(c.Custom),
    };

    public static List<(double T, double P)> ParseCustom(string text)
    {
        var pts = new List<(double, double)>();
        foreach (var part in text.Split(',', ';'))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 && double.TryParse(kv[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t)
                && double.TryParse(kv[1].Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double p))
                pts.Add((Math.Clamp(t, 0, 110), Math.Clamp(p, 0, 100)));
        }
        pts.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return pts.Count >= 2 ? pts : new() { (30, 30), (50, 40), (65, 60), (75, 80), (85, 100) };
    }

    public static double Eval(List<(double T, double P)> pts, double t)
    {
        if (t <= pts[0].T) return pts[0].P;
        for (int i = 1; i < pts.Count; i++)
            if (t <= pts[i].T)
            {
                var (t0, p0) = pts[i - 1]; var (t1, p1) = pts[i];
                return p0 + (p1 - p0) * (t - t0) / Math.Max(0.001, t1 - t0);
            }
        return pts[^1].P;
    }

    /// <summary>Um passo do controlador (chamado a cada ~1 s). Devolve o texto ao vivo.</summary>
    public static string Step(FanChannelView v, FanSettings g, float? cpu, float? gpu, double dt)
    {
        var o = v.Output;
        if (v.Config.Preset == "bios")
        {
            if (v.Duty >= 0) { try { o.SetDefault(); } catch { } v.Duty = -1; }
            return $"BIOS · {Rpm(o)}";
        }
        double? temp = v.Config.Source switch { "gpu" => gpu, "max" => Math.Max(cpu ?? 0, gpu ?? 0) is var m && m > 0 ? m : null, _ => cpu };
        double target;
        // Média móvel exponencial (EMA): alfa = 1 - e^(-dt/τ), igual para qualquer intervalo entre leituras. Os Ryzen X3D
        // pulam 10–15 °C por uma fração de segundo ao acordar um núcleo; com a EMA esse pico quase não chega à curva.
        double alpha = 1 - Math.Exp(-Math.Max(dt, 0.05) / Math.Max(g.EmaSeconds, 0.1));
        static double Ema(double prev, double x, double a) => double.IsNaN(prev) ? x : prev + (x - prev) * a;
        if (temp is double raw) v.SmoothT = Ema(v.SmoothT, raw, alpha);
        if (cpu is float rc) v.SmoothCpu = Ema(v.SmoothCpu, rc, alpha);
        // Segurança: sem leitura, CPU acima de 90 °C de forma sustentada (média) ou um pico real de 95 °C → 100%.
        // Antes o pico instantâneo de 90 °C já mandava 100% e a fan ficava minutos descendo devagar.
        bool hot = cpu is float c && (c >= 95 || v.SmoothCpu >= 90);
        if (temp is not double t || hot)
        {
            target = 100;
            v.Duty = 100;
        }
        else
        {
            double want = Eval(Curve(v.Config), v.SmoothT);
            if (v.Duty < 0) v.Duty = want;
            if (want > v.Duty)
            {
                v.Duty = Math.Min(want, v.Duty + g.RiseRate * dt);
                v.PeakT = v.SmoothT; v.LowFor = 0;
            }
            else if (want < v.Duty - 0.5)
            {
                // Histerese: só desacelera depois de a média cair HysteresisC (padrão 3 °C) abaixo do último pico.
                v.PeakT = Math.Max(v.PeakT, v.SmoothT);
                bool cooled = v.SmoothT <= v.PeakT - g.HysteresisC;
                v.LowFor = cooled ? v.LowFor + dt : 0;
                if (v.LowFor >= g.FallDelayS) v.Duty = Math.Max(want, v.Duty - g.FallRate * dt);
            }
            target = v.Duty;
        }
        float value = (float)Math.Clamp(target, Math.Max(o.Min, 0), o.Max <= 0 ? 100 : o.Max);
        try { o.Set(value); }
        catch (Exception ex) { return "Não aceitou o controle: " + ex.Message; }
        return $"{value:0}% · {Rpm(o)}" + (temp is double tt ? $" · {tt:0} °C ({(v.Config.Source == "gpu" ? "GPU" : v.Config.Source == "max" ? "maior" : "CPU")})" : "");
    }

    private static string Rpm(FanOutput o) { try { return o.RpmText(); } catch { return "sem leitura de RPM"; } }

    /// <summary>Devolve todas as saídas à BIOS.</summary>
    public static void ReleaseAll(IEnumerable<FanChannelView> views)
    {
        foreach (var v in views) { try { v.Output.SetDefault(); } catch { } v.Duty = -1; }
    }
}
