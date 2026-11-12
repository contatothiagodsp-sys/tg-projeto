using System.Globalization;

namespace ThgOptimization.Core.Intelligence;

/// <summary>Limiares das regras. Ficam num lugar só, com o motivo de cada um, para os testes e o relatório citarem os mesmos números.</summary>
public sealed record DiagnosisThresholds
{
    /// <summary>GPU "no limite" num segundo: utilização a partir de 95%.</summary>
    public double GpuSaturated { get; init; } = 95;
    /// <summary>Um núcleo "no limite" num segundo: a thread principal não tem mais para onde crescer.</summary>
    public double CoreSaturated { get; init; } = 90;
    /// <summary>CPU inteira ocupada.</summary>
    public double CpuSaturated { get; init; } = 85;
    /// <summary>Fração do tempo para chamar de gargalo dominante.</summary>
    public double DominantFraction { get; init; } = 0.7;
    /// <summary>RAM em uso ou commit a partir dos quais existe risco real de paginação.</summary>
    public double RamHigh { get; init; } = 92;
    public double CommitHigh { get; init; } = 90;
    public double VramHigh { get; init; } = 95;
    /// <summary>Sem limite informado pelo sensor: valores conservadores, abaixo do corte da maioria dos chips.</summary>
    public double DefaultCpuTempLimit { get; init; } = 95;
    public double DefaultGpuTempLimit { get; init; } = 87;
    /// <summary>"Perto do limite" = a esta distância dele.</summary>
    public double TempMargin { get; init; } = 3;
    /// <summary>Queda de clock, com carga comparável, que caracteriza limitação.</summary>
    public double ClockDrop { get; init; } = 0.05;
    /// <summary>Engasgos por minuto a partir dos quais a fluidez está comprometida, e razão P99 ÷ P50 que indica pacing ruim.</summary>
    public double StuttersPerMinute { get; init; } = 2;
    public double P99OverP50 { get; init; } = 2.5;
    /// <summary>Rede: perda (%), jitter (ms) e pico (ms) que se percebem em jogo online.</summary>
    public double NetLossPct { get; init; } = 2;
    public double NetJitterMs { get; init; } = 20;
    public double NetSpikeMs { get; init; } = 150;
    /// <summary>DPC acima disto (µs) já é audível/visível, segundo o critério do teste de engasgos do app.</summary>
    public double DpcMicroseconds { get; init; } = 1000;
    /// <summary>Medida externa mais velha que isto é tratada como velha.</summary>
    public TimeSpan ExternalMaxAge { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Diagnóstico de uma sessão. Cada regra só afirma o que os dados sustentam: associação é descrita como associação, medida ausente
/// vira limitação declarada, e "não foi possível concluir" é um resultado.
/// </summary>
public sealed class DiagnosisEngine
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private readonly DiagnosisThresholds _t;
    private readonly Func<DateTime> _now;

    public DiagnosisEngine(DiagnosisThresholds? thresholds = null, Func<DateTime>? now = null)
    {
        _t = thresholds ?? new DiagnosisThresholds();
        _now = now ?? (() => DateTime.Now);
    }

    public DiagnosisReport Diagnose(SessionTelemetry session)
    {
        var q = TelemetryQuality.Of(session);
        var findings = new List<Finding>();
        var ruledOut = new List<string>();
        var limits = new List<string>();
        var frames = Frames(session, q);

        // Erro de hardware é fato registrado pelo Windows: vale mesmo numa sessão curta.
        Stability(session, q, findings);

        if (!q.EnoughTime)
        {
            findings.Add(new Finding(DiagnosisKind.Inconclusive, Severity.Info, "Coleta curta demais para um diagnóstico",
                $"A sessão durou {Dur(q.Seconds)}. São necessários pelo menos {TelemetryQuality.MinSeconds / 60:0} minutos de jogo para separar um problema de uma oscilação normal.",
                new Confidence(1, new[] { "duração medida" }), Array.Empty<Evidence>(), Array.Empty<string>()));
            return new DiagnosisReport(session.Context, q, findings, ruledOut, limits, frames);
        }

        var f = new Features(session, q, frames, _t);
        if (q.Missing() is { Count: > 0 } missing) limits.Add("Sem leitura suficiente de: " + string.Join(", ", missing) + ".");
        if (!q.HasFrameCapture) limits.Add("Sem gravação de frames: percentis de frametime, 1% low e engasgos por frame não foram calculados.");
        else if (!q.SupportsLow01) limits.Add("Amostra pequena para 0,1% low (menos de 10 mil frames): o valor não é mostrado.");
        limits.Add("GPU Busy e tempo de CPU por frame não são medidos (exigiriam o serviço do PresentMon): o gargalo é inferido da utilização por segundo.");

        Limiter(f, findings, ruledOut);
        Thermal(f, findings, ruledOut, cpu: true);
        Thermal(f, findings, ruledOut, cpu: false);
        ProgressiveThermal(f, findings);
        PowerLimit(f, findings);
        Memory(f, findings, ruledOut);
        Vram(f, findings, ruledOut);
        bool pacing = FramePacing(f, findings, ruledOut);
        Background(f, findings, pacing);
        Dpc(f, findings, limits, pacing);
        Storage(f, findings, pacing);
        Network(f, findings, ruledOut, limits);

        // Engasgo sem nenhuma condição medida que o acompanhe: dizer que a causa não foi identificada é a resposta certa.
        if (pacing && !findings.Any(x => x.IsProblem && x.Kind is not (DiagnosisKind.FramePacing or DiagnosisKind.Network or DiagnosisKind.Stability)))
            findings.Add(new Finding(DiagnosisKind.Inconclusive, Severity.Attention, "Causa dos engasgos não identificada",
                "Houve perda de fluidez, mas nenhuma das condições medidas (CPU, GPU, memória, temperatura, programas de fundo) acompanha os momentos ruins. " +
                "Pode ser compilação de shaders, carregamento do próprio jogo ou algo que estes dados não observam.",
                new Confidence(1, new[] { "nenhuma associação encontrada nos dados" }), Array.Empty<Evidence>(),
                new[] { "DPC/ISR, disco e rede do servidor não são medidos segundo a segundo durante a partida." }));

        return new DiagnosisReport(session.Context, q, findings, ruledOut, limits, frames);
    }

    // ---------------------------------------------------------------- frames

    internal static FrameSummary? Frames(SessionTelemetry s, TelemetryQuality q)
    {
        if (q.HasFrameCapture)
        {
            var buf = new double[s.FrameTimesMs.Count];
            for (int i = 0; i < buf.Length; i++) buf[i] = s.FrameTimesMs[i];
            var st = FrameStatistics.FromFrameTimes(buf);
            return new FrameSummary(st.Frames, st.AvgFps, st.P50Ms, st.P95Ms, st.P99Ms,
                                    q.SupportsLow1 ? st.Low1AvgFps : null, q.SupportsLow01 ? st.Low01AvgFps : null,
                                    st.Stutters, st.StuttersPerMinute, st.Histogram, MetricOrigin.Captured);
        }
        var fps = s.Points.Where(p => p.Fps > 1).Select(p => p.Fps!.Value).ToArray();
        if (fps.Length < 30) return null;
        // Só com o FPS por segundo: média e mediana existem; percentis por frame, não. Nada é inventado no lugar.
        double median = RobustStats.Median(fps);
        return new FrameSummary(0, fps.Average(), 1000 / median, double.NaN, double.NaN, null, null, 0, double.NaN,
                                Array.Empty<int>(), MetricOrigin.Estimated);
    }

    // ---------------------------------------------------------------- regras

    private void Stability(SessionTelemetry s, TelemetryQuality q, List<Finding> findings)
    {
        int errors = s.Points.Count(p => p.HardwareError);
        if (errors == 0) return;
        var first = s.Points.First(p => p.HardwareError);
        findings.Add(new Finding(DiagnosisKind.Stability, Severity.Problem, "Erro de hardware durante a partida",
            "O Windows registrou erro de hardware (WHEA) ou reinício do driver de vídeo durante a sessão. Isso indica instabilidade: " +
            "overclock/undervolt no limite, memória (XMP/EXPO) instável, temperatura ou fonte. Não é um problema de otimização.",
            Confidence.Build(0.95, q, Array.Empty<Metric>(), timeMatters: false, notes: new[] { "evento registrado pelo Windows, não é inferência" }),
            new[] { new Evidence("Erros de hardware", errors.ToString(Pt), $"primeiro aos {Dur(first.AtSeconds)} de sessão") },
            new[] { "O registro diz que houve o erro, não qual componente falhou: veja Overclock › erros recentes." }));
    }

    private void Limiter(Features f, List<Finding> findings, List<string> ruledOut)
    {
        var q = f.Quality;
        if (!q.Has(Metric.GpuLoad) && !q.Has(Metric.CpuLoad)) return;

        double gpuSat = f.Fraction(p => p.GpuLoad >= _t.GpuSaturated, p => p.GpuLoad != null);
        double coreSat = f.Fraction(p => p.CpuMaxCoreLoad >= _t.CoreSaturated, p => p.CpuMaxCoreLoad != null);
        double cpuSat = f.Fraction(p => p.CpuLoad >= _t.CpuSaturated, p => p.CpuLoad != null);
        double gpuMedian = f.Median(Metric.GpuLoad), cpuMedian = f.Median(Metric.CpuLoad), coreMedian = f.Median(Metric.CpuMaxCoreLoad);

        if (q.Has(Metric.GpuLoad) && gpuSat >= _t.DominantFraction)
        {
            var ev = new List<Evidence>
            {
                new("Uso da GPU", $"≥ {_t.GpuSaturated:0}% em {Pct(gpuSat)} do tempo", "a placa de vídeo trabalhou no limite durante quase toda a sessão"),
            };
            if (q.Has(Metric.CpuMaxCoreLoad)) ev.Add(new("Núcleo mais ocupado", $"mediana {coreMedian:0}%", coreSat < 0.3 ? "a CPU teve folga" : "a CPU também esteve ocupada"));
            findings.Add(new Finding(DiagnosisKind.GpuBound, Severity.Info, "A GPU é o limite deste jogo",
                "A placa de vídeo ficou no limite na maior parte do tempo. É o cenário esperado em jogo pesado: ela está sendo aproveitada. " +
                "Mais FPS só vem de menos carga gráfica (resolução, qualidade, DLSS/FSR). Ajustes de CPU ou do Windows não mudam este limite.",
                Confidence.Build(Scale(gpuSat, _t.DominantFraction, 1, 0.75, 1), q, new[] { Metric.GpuLoad }, cap: 0.9),
                ev, new[] { "Utilização de 99% não mede sozinha o trabalho por frame (GPU Busy): a conclusão vem da persistência ao longo da sessão." }));
            return;
        }

        bool gpuHasRoom = q.Has(Metric.GpuLoad) && gpuMedian < 85;
        if (gpuHasRoom && q.Has(Metric.CpuLoad) && cpuSat >= 0.5)
        {
            findings.Add(new Finding(DiagnosisKind.CpuBound, Severity.Attention, "A CPU é o limite deste jogo",
                "A CPU inteira ficou ocupada enquanto a GPU tinha folga: o processador não entrega frames no ritmo que a placa aguenta. " +
                "Reduzir opções que pesam na CPU (distância de visão, multidões, física) e fechar programas de fundo ajuda; subir a resolução quase não custa FPS.",
                Confidence.Build(Scale(cpuSat, 0.5, 1, 0.7, 0.95), q, new[] { Metric.CpuLoad, Metric.GpuLoad }, cap: 0.85),
                new[]
                {
                    new Evidence("Uso da CPU", $"≥ {_t.CpuSaturated:0}% em {Pct(cpuSat)} do tempo", "todos os núcleos ocupados"),
                    new Evidence("Uso da GPU", $"mediana {gpuMedian:0}%", "a placa de vídeo esperou pela CPU"),
                },
                Array.Empty<string>()));
            return;
        }

        if (gpuHasRoom && q.Has(Metric.CpuMaxCoreLoad) && coreSat >= 0.5)
        {
            findings.Add(new Finding(DiagnosisKind.MainThreadBound, Severity.Attention, "Uma thread da CPU é o limite deste jogo",
                $"O uso total da CPU ficou em {cpuMedian:0}%, mas um núcleo esteve no limite em {Pct(coreSat)} do tempo, com a GPU sobrando. " +
                "O jogo depende de uma thread principal: o que conta é o desempenho por núcleo, não a quantidade de núcleos.",
                Confidence.Build(Scale(coreSat, 0.5, 1, 0.7, 0.95), q, new[] { Metric.CpuMaxCoreLoad, Metric.GpuLoad }, cap: 0.85),
                new[]
                {
                    new Evidence("Núcleo mais ocupado", $"≥ {_t.CoreSaturated:0}% em {Pct(coreSat)} do tempo", "a thread principal não tem mais para onde crescer"),
                    new Evidence("Uso total da CPU", $"mediana {cpuMedian:0}%", "a média esconde o núcleo saturado"),
                    new Evidence("Uso da GPU", $"mediana {gpuMedian:0}%", "a placa de vídeo esperou pela CPU"),
                },
                new[] { "O núcleo mais ocupado é lido uma vez por segundo: não identifica qual thread do jogo é a responsável." }));
            return;
        }

        // Nada no limite. Se o FPS está parado num valor típico de trava, é isso; senão, simplesmente não há gargalo dominante.
        if (f.FpsCap(_t) is int cap)
            ruledOut.Add($"FPS estável em torno de {cap}: nem CPU nem GPU no limite. É trava de FPS, V-Sync ou VRR atuando, não um gargalo.");
        else if (q.Has(Metric.GpuLoad) && q.Has(Metric.CpuLoad))
            ruledOut.Add($"Nenhum gargalo dominante: GPU em {gpuMedian:0}% e CPU em {cpuMedian:0}% (medianas), sem componente preso no limite.");
    }

    private void Thermal(Features f, List<Finding> findings, List<string> ruledOut, bool cpu)
    {
        var q = f.Quality;
        var (temp, clock, load) = cpu ? (Metric.CpuTemp, Metric.CpuClock, Metric.CpuLoad) : (Metric.GpuTemp, Metric.GpuClock, Metric.GpuLoad);
        if (!q.Has(temp)) return;
        double limit = (cpu ? f.Session.External.CpuTempLimit : f.Session.External.GpuTempLimit) ?? (cpu ? _t.DefaultCpuTempLimit : _t.DefaultGpuTempLimit);
        bool knownLimit = (cpu ? f.Session.External.CpuTempLimit : f.Session.External.GpuTempLimit) != null;
        double hotAt = limit - _t.TempMargin;
        double minLoad = cpu ? 40 : 80;
        string part = cpu ? "CPU" : "GPU";

        double hotFrac = f.Fraction(p => p.Get(temp) >= hotAt, p => p.Get(temp) != null);
        double peak = f.Session.Points.Max(p => p.Get(temp) ?? double.MinValue);
        if (hotFrac < 0.05)
        {
            ruledOut.Add($"Temperatura da {part} sob controle: pico de {peak:0} °C, abaixo da faixa de limitação ({hotAt:0} °C).");
            return;
        }

        var ev = new List<Evidence> { new($"Temperatura da {part}", $"≥ {hotAt:0} °C em {Pct(hotFrac)} do tempo (pico {peak:0} °C)",
                                          knownLimit ? $"limite informado pelo sensor: {limit:0} °C" : $"limite de referência: {limit:0} °C (o sensor não informa o do chip)") };
        var limitsText = new List<string>();
        if (!knownLimit) limitsText.Add("O limite térmico exato deste chip não foi lido: a faixa usada é uma referência conservadora.");

        // Limitação comprovada = clock menor nos segundos quentes do que nos frios, com carga comparável.
        var hot = f.Values(clock, p => p.Get(temp) >= hotAt && p.Get(load) >= minLoad);
        var cool = f.Values(clock, p => p.Get(temp) < hotAt && p.Get(load) >= minLoad);
        if (q.Has(clock) && hot.Count >= 20 && cool.Count >= 20)
        {
            double hotClock = RobustStats.Median(hot), coolClock = RobustStats.Median(cool), drop = coolClock > 0 ? 1 - hotClock / coolClock : 0;
            ev.Add(new($"Clock da {part}", $"{coolClock:0} MHz frio → {hotClock:0} MHz quente ({drop * -100:+0.#;-0.#}%)", "comparação com carga parecida"));
            if (drop >= _t.ClockDrop)
            {
                findings.Add(new Finding(cpu ? DiagnosisKind.CpuTemperature : DiagnosisKind.GpuTemperature, Severity.Problem,
                    $"{part} limitada por temperatura",
                    $"Quando a {part} chega perto do limite térmico, o clock cai {drop * 100:0.#}% com a mesma carga. É limitação térmica: o chip reduz a frequência para se proteger. " +
                    "Não recomendamos mexer em clock ou tensão. Revise fluxo de ar, poeira, pasta térmica e a curva das ventoinhas.",
                    Confidence.Build(Scale(drop, _t.ClockDrop, 0.15, 0.8, 1), q, new[] { temp, clock }), ev, limitsText));
                return;
            }
            ruledOut.Add($"{part} quente ({Pct(hotFrac)} do tempo perto do limite), mas o clock não caiu: temperatura alta sem limitação comprovada.");
            return;
        }

        if (hotFrac < 0.25) return;
        limitsText.Add(q.Has(clock) ? "Não houve tempo suficiente fora da faixa quente para comparar o clock."
                                    : $"Sem leitura do clock da {part}: não dá para comprovar se a frequência caiu.");
        findings.Add(new Finding(cpu ? DiagnosisKind.CpuTemperature : DiagnosisKind.GpuTemperature, Severity.Attention,
            $"{part} trabalhando perto do limite térmico",
            $"A {part} passou {Pct(hotFrac)} da sessão perto do limite. Temperatura alta não é o mesmo que limitação: sem a comparação de clock, a perda de desempenho não está comprovada. " +
            "Vale conferir a ventilação antes de qualquer outro ajuste.",
            Confidence.Build(0.5, q, new[] { temp }, cap: 0.55), ev, limitsText));
    }

    /// <summary>Perda de fluidez que aparece com o tempo de jogo: FPS e clock caem enquanto a temperatura sobe.</summary>
    private void ProgressiveThermal(Features f, List<Finding> findings)
    {
        var q = f.Quality;
        if (q.Seconds < 15 * 60 || !q.Has(Metric.Fps)) return;
        if (findings.Any(x => x.Kind is DiagnosisKind.CpuTemperature or DiagnosisKind.GpuTemperature && x.Severity == Severity.Problem)) return;
        foreach (bool cpu in new[] { false, true })
        {
            var (temp, clock) = cpu ? (Metric.CpuTemp, Metric.CpuClock) : (Metric.GpuTemp, Metric.GpuClock);
            if (!q.Has(temp) || !q.Has(clock)) continue;
            var (fps0, fps1) = f.Thirds(Metric.Fps);
            var (t0, t1) = f.Thirds(temp);
            var (c0, c1) = f.Thirds(clock);
            if (double.IsNaN(fps0) || double.IsNaN(t0) || double.IsNaN(c0) || fps0 <= 0 || c0 <= 0) continue;
            double fpsDrop = 1 - fps1 / fps0, clockDrop = 1 - c1 / c0, rise = t1 - t0;
            if (fpsDrop < 0.08 || clockDrop < 0.03 || rise < 5) continue;
            string part = cpu ? "CPU" : "GPU";
            double? onset = f.Onset(clock, c0 * 0.97);
            string when = onset is double o ? $"após aproximadamente {o / 60:0} minutos" : "ao longo da sessão";
            findings.Add(new Finding(DiagnosisKind.ThermalThrottling, Severity.Problem, $"Perda de fluidez {when} de jogo",
                $"O FPS caiu {fpsDrop * 100:0}% do começo para o trecho final da sessão, junto com redução de {clockDrop * 100:0.#}% no clock da {part} e aumento de {rise:0} °C na temperatura. " +
                "O conjunto é compatível com limitação térmica progressiva. Não recomendamos alterar clocks: revise a ventilação e a curva das ventoinhas.",
                Confidence.Build(Scale(fpsDrop, 0.08, 0.25, 0.7, 0.9), q, new[] { Metric.Fps, temp, clock }, cap: 0.8,
                                 notes: new[] { "três medidas independentes mudaram juntas; a cena do jogo também pode ter mudado" }),
                new[]
                {
                    new Evidence("FPS", $"{fps0:0} no primeiro terço → {fps1:0} no trecho final", "mediana de cada trecho"),
                    new Evidence($"Clock da {part}", $"{c0:0} → {c1:0} MHz", "redução progressiva"),
                    new Evidence($"Temperatura da {part}", $"{t0:0} → {t1:0} °C", "aquecimento ao longo da sessão"),
                },
                new[] { "Associação no tempo, não prova de causa: um mapa ou trecho mais pesado no fim da sessão produz efeito parecido no FPS." }));
            return;
        }
    }

    private void PowerLimit(Features f, List<Finding> findings)
    {
        var q = f.Quality;
        if (!q.Has(Metric.CpuPower) || !q.Has(Metric.CpuClock) || !q.Has(Metric.CpuTemp) || !q.Has(Metric.CpuMaxCoreLoad)) return;
        if (findings.Any(x => x.Kind is DiagnosisKind.CpuTemperature or DiagnosisKind.ThermalThrottling)) return; // a temperatura já explica o clock
        double hotAt = (f.Session.External.CpuTempLimit ?? _t.DefaultCpuTempLimit) - _t.TempMargin - 7;
        var busy = f.Session.Points.Where(p => p.CpuMaxCoreLoad >= _t.CoreSaturated && p.CpuPowerW != null && p.CpuClockMhz != null && p.CpuTemp < hotAt).ToList();
        if (busy.Count < 120) return;
        var power = busy.Select(p => p.CpuPowerW!.Value).OrderBy(v => v).ToArray();
        double p50 = RobustStats.PercentileSorted(power, 0.5), p95 = RobustStats.PercentileSorted(power, 0.95);
        double busyClock = RobustStats.Median(busy.Select(p => p.CpuClockMhz!.Value));
        double topClock = RobustStats.Percentile(f.Values(Metric.CpuClock, _ => true), 0.99);
        // Consumo "cravado" num teto (mediana colada no P95) com clock abaixo do que o chip já mostrou, sem estar quente.
        if (p95 <= 0 || p50 < 0.97 * p95 || topClock <= 0 || busyClock > 0.93 * topClock) return;
        findings.Add(new Finding(DiagnosisKind.PowerLimit, Severity.Attention, "Possível limite de potência da CPU",
            $"Sob carga, o consumo da CPU fica parado em torno de {p50:0} W e o clock em {busyClock:0} MHz, abaixo dos {topClock:0} MHz que o chip atinge, sem temperatura alta. " +
            "É compatível com um limite de potência (PL1/PPT) da placa-mãe ou do plano de energia. O tHg não altera esses limites: confira o plano de energia e a BIOS.",
            Confidence.Build(0.7, q, new[] { Metric.CpuPower, Metric.CpuClock, Metric.CpuTemp }, cap: 0.6),
            new[]
            {
                new Evidence("Consumo da CPU sob carga", $"mediana {p50:0} W, P95 {p95:0} W", "consumo preso num teto"),
                new Evidence("Clock sob carga", $"{busyClock:0} MHz de {topClock:0} MHz", "frequência abaixo do máximo observado"),
            },
            new[] { "Os limites configurados no firmware não são lidos: a conclusão é por comportamento, não por leitura do limite." }));
    }

    private void Memory(Features f, List<Finding> findings, List<string> ruledOut)
    {
        var q = f.Quality;
        if (!q.Has(Metric.Ram) && !q.Has(Metric.Commit)) return;
        bool High(TelemetryPoint p) => p.RamPct >= _t.RamHigh || p.CommitPct >= _t.CommitHigh;
        double frac = f.Fraction(High, p => p.RamPct != null || p.CommitPct != null);
        double median = f.Median(Metric.Ram);
        var assoc = f.Associate(High);
        if (frac < 0.05)
        {
            if (median >= 75) ruledOut.Add($"RAM em {median:0}% (mediana) sem sinal de pressão: é uso normal, o Windows aproveita memória livre como cache.");
            else if (!double.IsNaN(median)) ruledOut.Add($"Memória estável: RAM em {median:0}% (mediana), sem pressão.");
            return;
        }
        var ev = new List<Evidence> { new("Memória no limite", $"{Pct(frac)} do tempo", $"RAM ≥ {_t.RamHigh:0}% ou commit ≥ {_t.CommitHigh:0}%") };
        var notes = new List<string>();
        double strength = Scale(frac, 0.05, 0.5, 0.45, 0.7);
        if (assoc.Associated)
        {
            ev.Add(new("Engasgos com a memória no limite", $"{Pct(assoc.WhenBad)} dos momentos ruins (contra {Pct(assoc.WhenOk)} no resto)", "os engasgos se concentram quando falta memória"));
            strength += 0.25;
            notes.Add("os engasgos coincidem com a falta de memória");
        }
        if (q.Has(Metric.Ram) && f.SlopePerHour(Metric.Ram) is double slope && slope >= 8 && q.Seconds >= 20 * 60)
            ev.Add(new("Crescimento da RAM", $"+{slope:0.#} pontos percentuais por hora", "uso crescendo durante a sessão: possível vazamento no jogo ou em um programa aberto"));
        findings.Add(new Finding(DiagnosisKind.MemoryPressure, assoc.Associated ? Severity.Problem : Severity.Attention, "Pressão de memória RAM",
            "A memória ficou no limite durante a partida. Nessa condição o Windows passa a usar o arquivo de paginação e o jogo pode engasgar. " +
            "Feche navegadores e programas pesados antes de jogar. Limpar a memória em espera não resolve falta real de RAM.",
            Confidence.Build(strength, q, new[] { q.Has(Metric.Ram) ? Metric.Ram : Metric.Commit }, notes: notes), ev,
            new[] { "Paginação e page faults por processo não são medidos: a pressão é inferida de RAM em uso e commit." }));
    }

    private void Vram(Features f, List<Finding> findings, List<string> ruledOut)
    {
        var q = f.Quality;
        if (!q.Has(Metric.Vram)) return;
        bool High(TelemetryPoint p) => p.VramPct >= _t.VramHigh;
        double frac = f.Fraction(High, p => p.VramPct != null);
        double peak = f.Session.Points.Max(p => p.VramPct ?? 0);
        if (frac < 0.1)
        {
            ruledOut.Add($"Memória de vídeo com folga: pico de {peak:0}%.");
            return;
        }
        var assoc = f.Associate(High);
        var ev = new List<Evidence> { new("VRAM", $"≥ {_t.VramHigh:0}% em {Pct(frac)} do tempo (pico {peak:0}%)", "memória de vídeo praticamente cheia") };
        if (assoc.Associated) ev.Add(new("Engasgos com a VRAM cheia", $"{Pct(assoc.WhenBad)} dos momentos ruins (contra {Pct(assoc.WhenOk)} no resto)", "associação no tempo"));
        findings.Add(new Finding(DiagnosisKind.VramPressure, assoc.Associated ? Severity.Problem : Severity.Attention, "Memória de vídeo no limite",
            "A VRAM ficou praticamente cheia. Quando ela acaba, o driver troca texturas com a RAM e o jogo engasga. Reduzir a qualidade das texturas ou a resolução é o ajuste proporcional.",
            Confidence.Build(Scale(frac, 0.1, 0.6, 0.55, 0.7) + (assoc.Associated ? 0.25 : 0), q, new[] { Metric.Vram }, cap: assoc.Associated ? 0.9 : 0.65),
            ev, new[] { "VRAM alocada não é igual a VRAM necessária: alguns jogos reservam tudo o que existe sem precisar." }));
    }

    private bool FramePacing(Features f, List<Finding> findings, List<string> ruledOut)
    {
        var fr = f.Frames;
        if (fr is not { Origin: MetricOrigin.Captured } || fr.Frames < TelemetryQuality.MinFramesForLow1) return false;
        double ratio = fr.P50Ms > 0 ? fr.P99Ms / fr.P50Ms : 0;
        if (fr.StuttersPerMinute < _t.StuttersPerMinute && ratio < _t.P99OverP50)
        {
            ruledOut.Add($"Fluidez consistente: P99 de {fr.P99Ms:0.0} ms para mediana de {fr.P50Ms:0.0} ms, {fr.StuttersPerMinute:0.#} engasgo(s) por minuto.");
            return false;
        }
        var ev = new List<Evidence>
        {
            new("Engasgos", $"{fr.Stutters} ({fr.StuttersPerMinute:0.#} por minuto)", "frames acima de 2,5× a mediana e de 25 ms"),
            new("Frametime", $"P50 {fr.P50Ms:0.0} ms · P95 {fr.P95Ms:0.0} ms · P99 {fr.P99Ms:0.0} ms", $"P99 é {ratio:0.0}× a mediana"),
        };
        if (fr.Low1Fps is double low) ev.Add(new("1% low", $"{low:0} FPS para {fr.AvgFps:0} FPS de média", "distância entre a média e os piores momentos"));
        findings.Add(new Finding(DiagnosisKind.FramePacing, Severity.Problem, "Frames irregulares (frame pacing)",
            "O FPS médio não conta a história: os frames chegam em intervalos irregulares, e é isso que se percebe como travadinha. " +
            "Veja no histórico da sessão os momentos exatos e o que o PC fazia em cada um.",
            Confidence.Build(Scale(Math.Max(fr.StuttersPerMinute / _t.StuttersPerMinute, ratio / _t.P99OverP50), 1, 3, 0.8, 1), f.Quality, new[] { Metric.Fps },
                             notes: new[] { $"{fr.Frames:N0} frames medidos diretamente (ETW)" }),
            ev, Array.Empty<string>()));
        return true;
    }

    private static void Background(Features f, List<Finding> findings, bool pacing)
    {
        var hogs = f.Session.External.BackgroundHogs;
        if (hogs.Count == 0) return;
        string names = string.Join(", ", hogs.OrderByDescending(h => h.Strikes).Take(4).Select(h => h.Name));
        findings.Add(new Finding(DiagnosisKind.ConcurrentProcesses, pacing ? Severity.Problem : Severity.Attention, "Programas de fundo disputaram a CPU",
            $"Durante a partida, {hogs.Count} programa(s) passaram de 10% da CPU por leituras seguidas: {names}. " +
            "O tHg não encerra programas de terceiros: decida se vale fechá-los antes de jogar.",
            Confidence.Build(pacing ? 0.8 : 0.6, f.Quality, Array.Empty<Metric>(), cap: 0.8, notes: new[] { "consumo medido por processo pelo Modo Jogo" }),
            hogs.OrderByDescending(h => h.Strikes).Take(6).Select(h => new Evidence(h.Name, $"{h.Strikes} leitura(s) acima de 10% da CPU", "programa de fundo ativo durante o jogo")).ToList(),
            new[] { "O momento exato de cada pico de CPU desses programas não é cruzado frame a frame com os engasgos." }));
    }

    private void Dpc(Features f, List<Finding> findings, List<string> limits, bool pacing)
    {
        var x = f.Session.External;
        if (x.DpcMaxMicroseconds is not double dpc || x.DpcMeasuredAt is not DateTime at) return;
        if (_now() - at > _t.ExternalMaxAge)
        {
            limits.Add($"A medição de DPC/ISR é de {at:dd/MM/yyyy}: velha demais para entrar no diagnóstico. Refaça em Desempenho › Engasgos.");
            return;
        }
        if (dpc < _t.DpcMicroseconds || !pacing) return;
        string driver = string.IsNullOrEmpty(x.DpcWorstDriver) ? "" : $" O driver com maior tempo foi {x.DpcWorstDriver}.";
        findings.Add(new Finding(DiagnosisKind.DpcIsr, Severity.Attention, "Latência de driver (DPC/ISR) elevada",
            $"O teste de engasgos mediu DPC de até {dpc:0} µs, acima de {_t.DpcMicroseconds:0} µs.{driver} Latência de driver causa microtravadas que não aparecem no uso de CPU ou GPU.",
            Confidence.Build(0.7, f.Quality, Array.Empty<Metric>(), cap: 0.6, timeMatters: false, notes: new[] { $"medido em {at:dd/MM/yyyy}, fora desta sessão" }),
            new[] { new Evidence("Maior DPC", $"{dpc:0} µs", $"medido em {at:dd/MM/yyyy}") },
            new[] { "A medida foi feita fora da partida: indica uma condição do sistema, não o que aconteceu neste jogo." }));
    }

    private static void Storage(Features f, List<Finding> findings, bool pacing)
    {
        if (f.Session.External.GameOnHdd != true || !pacing) return;
        findings.Add(new Finding(DiagnosisKind.Storage, Severity.Attention, "Jogo instalado em disco mecânico (HD)",
            "O jogo está em um HD, e houve engasgos. Carregar texturas e áreas do mapa de um disco mecânico é uma causa comum de travadas em jogos atuais. Mover o jogo para um SSD é a solução proporcional.",
            Confidence.Build(0.5, f.Quality, Array.Empty<Metric>(), cap: 0.5, notes: new[] { "tipo de disco identificado; latência de disco durante a partida não é medida" }),
            new[] { new Evidence("Disco do jogo", "HD mecânico", "identificado pelo tipo de mídia do volume") },
            new[] { "A atividade de disco no momento de cada engasgo não foi medida." }));
    }

    private void Network(Features f, List<Finding> findings, List<string> ruledOut, List<string> limits)
    {
        var net = NetworkQuality.Analyze(f.Session.Points);
        if (net == null) return;
        limits.Add("Rede: a sonda mede até o roteador e um servidor público de referência, não o servidor do jogo.");
        if (!net.IsProblem(_t))
        {
            ruledOut.Add($"Rede estável: {net.MedianMs:0} ms (mediana), variação de {net.JitterMs:0} ms, {net.LossPct:0.#}% de perda.");
            return;
        }
        findings.Add(new Finding(DiagnosisKind.Network, Severity.Attention, "Conexão instável durante a partida",
            "A conexão oscilou durante a sessão. Isso causa lag de rede (teleporte, atraso nos comandos), mas não derruba o FPS: são problemas diferentes. " +
            "O tHg não altera DNS, rotas nem TCP/IP. Prefira cabo ao Wi-Fi e confira o que mais usa a rede.",
            Confidence.Build(net.LossPct >= _t.NetLossPct ? 0.85 : 0.7, f.Quality, Array.Empty<Metric>(), cap: 0.8, timeMatters: false,
                             notes: new[] { $"{net.Probes} sondas durante a sessão" }),
            new[]
            {
                new Evidence("Latência", $"mediana {net.MedianMs:0} ms · P95 {net.P95Ms:0} ms", "tempo de resposta da sonda"),
                new Evidence("Variação (jitter)", $"{net.JitterMs:0} ms", "diferença típica entre sondas seguidas"),
                new Evidence("Perda", $"{net.LossPct:0.#}% ({net.Lost} de {net.Probes})", "sondas sem resposta"),
            },
            new[] { "A causa (Wi-Fi, provedor, roteador) não é identificada por esta medida." }));
    }

    // ---------------------------------------------------------------- utilitários

    private static double Scale(double v, double fromLo, double fromHi, double toLo, double toHi) =>
        toLo + (toHi - toLo) * Math.Clamp((v - fromLo) / (fromHi - fromLo), 0, 1);

    private static string Pct(double fraction) => (fraction * 100).ToString("0", Pt) + "%";

    private static string Dur(double seconds) => seconds >= 3600 ? $"{seconds / 3600:0.#} h" : seconds >= 60 ? $"{seconds / 60:0} min" : $"{seconds:0} s";

    /// <summary>Contas compartilhadas pelas regras, feitas uma vez por sessão.</summary>
    internal sealed class Features
    {
        public SessionTelemetry Session { get; }
        public TelemetryQuality Quality { get; }
        public FrameSummary? Frames { get; }
        private readonly bool[] _bad;
        private readonly int _badCount;

        public Features(SessionTelemetry s, TelemetryQuality q, FrameSummary? frames, DiagnosisThresholds t)
        {
            Session = s;
            Quality = q;
            Frames = frames;
            // Segundo ruim: teve engasgo contado ou o pior frame passou do critério de pico (2,5× a mediana e 25 ms).
            double median = frames is { P50Ms: > 0 } ? frames.P50Ms : 0;
            double spike = Math.Max(FrameStatistics.StutterMedianFactor * median, FrameStatistics.StutterMinMs);
            _bad = new bool[s.Points.Count];
            for (int i = 0; i < _bad.Length; i++)
            {
                var p = s.Points[i];
                _bad[i] = p.Stutters > 0 || (median > 0 && p.WorstFrameMs > spike);
                if (_bad[i]) _badCount++;
            }
        }

        public double Fraction(Func<TelemetryPoint, bool> cond, Func<TelemetryPoint, bool> known)
        {
            int n = 0, hit = 0;
            foreach (var p in Session.Points)
            {
                if (!known(p)) continue;
                n++;
                if (cond(p)) hit++;
            }
            return n == 0 ? 0 : hit / (double)n;
        }

        public List<double> Values(Metric m, Func<TelemetryPoint, bool> where)
        {
            var list = new List<double>();
            foreach (var p in Session.Points)
                if (p.Get(m) is double v && where(p)) list.Add(v);
            return list;
        }

        public double Median(Metric m) => RobustStats.Median(Values(m, _ => true));

        /// <summary>Mediana do primeiro terço e do trecho final (os últimos 5 min, ou um sexto da sessão se for maior): uma queda que só aparece no fim não se dilui.</summary>
        public (double First, double Last) Thirds(Metric m)
        {
            int n = Session.Points.Count, third = n / 3, tail = Math.Min(third, Math.Max(300, n / 6));
            if (third < 30) return (double.NaN, double.NaN);
            var first = Session.Points.Take(third).Select(p => p.Get(m)).Where(v => v != null).Select(v => v!.Value).ToList();
            var last = Session.Points.Skip(n - tail).Select(p => p.Get(m)).Where(v => v != null).Select(v => v!.Value).ToList();
            return first.Count < 15 || last.Count < 15 ? (double.NaN, double.NaN) : (RobustStats.Median(first), RobustStats.Median(last));
        }

        /// <summary>Primeiro instante (s) a partir do qual a mediana móvel de 60 s fica abaixo do piso e não volta mais.</summary>
        public double? Onset(Metric m, double floor)
        {
            const int Window = 60;
            var pts = Session.Points;
            double? onset = null;
            for (int i = 0; i + Window <= pts.Count; i += 15)
            {
                var w = new List<double>();
                for (int k = i; k < i + Window; k++) if (pts[k].Get(m) is double v) w.Add(v);
                if (w.Count < Window / 2) continue;
                if (RobustStats.Median(w) < floor) onset ??= pts[i].AtSeconds - pts[0].AtSeconds;
                else onset = null;
            }
            return onset;
        }

        public double? SlopePerHour(Metric m)
        {
            var x = new List<double>();
            var y = new List<double>();
            // Uma amostra a cada 30 s basta para a tendência e mantém a conta pequena.
            for (int i = 0; i < Session.Points.Count; i += 30)
                if (Session.Points[i].Get(m) is double v) { x.Add(Session.Points[i].AtSeconds); y.Add(v); }
            double slope = RobustStats.TheilSenSlope(x, y);
            return double.IsNaN(slope) ? null : slope * 3600;
        }

        public readonly record struct Association(bool Associated, double WhenBad, double WhenOk, int BadSeconds);

        /// <summary>
        /// A condição aparece mais nos segundos ruins do que nos bons? Olha também os 2 s anteriores (a causa vem antes do engasgo).
        /// Associada = pelo menos 5 segundos ruins, condição presente em metade deles e 25 pontos acima do resto. É associação, não causa.
        /// </summary>
        public Association Associate(Func<TelemetryPoint, bool> cond)
        {
            if (_badCount < 5) return new Association(false, 0, 0, _badCount);
            int badHit = 0, okHit = 0, ok = 0;
            var pts = Session.Points;
            for (int i = 0; i < pts.Count; i++)
            {
                bool near = cond(pts[i]) || (i > 0 && cond(pts[i - 1])) || (i > 1 && cond(pts[i - 2]));
                if (_bad[i]) { if (near) badHit++; }
                else { ok++; if (near) okHit++; }
            }
            double whenBad = badHit / (double)_badCount, whenOk = ok == 0 ? 0 : okHit / (double)ok;
            return new Association(whenBad >= 0.5 && whenBad - whenOk >= 0.25, whenBad, whenOk, _badCount);
        }

        /// <summary>FPS parado num valor típico de trava (variação menor que 4% e a até 3% de um valor comum ou da taxa do monitor).</summary>
        public int? FpsCap(DiagnosisThresholds t)
        {
            var fps = Values(Metric.Fps, p => p.Fps > 1);
            if (fps.Count < 60) return null;
            double median = RobustStats.Median(fps), mad = RobustStats.Mad(fps);
            if (median <= 0 || mad / median > 0.04) return null;
            var caps = new List<int> { 30, 40, 45, 60, 72, 75, 90, 100, 120, 144, 165, 180, 200, 240, 360 };
            if (Session.External.RefreshHz is int hz and > 0) { caps.Add(hz); caps.Add(hz - 3); caps.Add(hz / 2); }
            foreach (int cap in caps.OrderByDescending(c => c))
                if (Math.Abs(median - cap) / cap <= 0.03) return cap;
            return null;
        }
    }
}

/// <summary>Qualidade da conexão a partir das sondas de uma sessão. Separado do diagnóstico gráfico: rede ruim não é FPS baixo.</summary>
public sealed record NetworkQuality(int Probes, int Lost, double MedianMs, double P95Ms, double JitterMs)
{
    public const int MinProbes = 20;
    public double LossPct => Probes == 0 ? 0 : 100.0 * Lost / Probes;

    public bool IsProblem(DiagnosisThresholds t) =>
        LossPct >= t.NetLossPct || JitterMs >= t.NetJitterMs || (P95Ms >= t.NetSpikeMs && P95Ms >= 2 * MedianMs);

    /// <summary>Null = sondas insuficientes: sem diagnóstico de rede, em vez de um palpite.</summary>
    public static NetworkQuality? Analyze(IReadOnlyList<TelemetryPoint> points)
    {
        var answered = new List<double>();
        int probes = 0, lost = 0;
        foreach (var p in points)
        {
            if (p.NetLost == null && p.NetLatencyMs == null) continue;
            probes++;
            if (p.NetLost == true || p.NetLatencyMs == null) lost++;
            else answered.Add(p.NetLatencyMs.Value);
        }
        if (probes < MinProbes) return null;
        if (answered.Count == 0) return new NetworkQuality(probes, lost, double.NaN, double.NaN, double.NaN);
        var diffs = new List<double>();
        for (int i = 1; i < answered.Count; i++) diffs.Add(Math.Abs(answered[i] - answered[i - 1]));
        return new NetworkQuality(probes, lost, RobustStats.Median(answered), RobustStats.Percentile(answered, 0.95),
                                  diffs.Count == 0 ? 0 : RobustStats.Median(diffs));
    }
}
