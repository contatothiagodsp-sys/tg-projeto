using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ThgOptimization;

/// <summary>Um driver medido no teste: quantas vezes segurou a CPU, o pior tempo e o que fazer.</summary>
public sealed record DriverLatency(string File, string What, string Fix, int Count, double MaxUs, int Over500, int Over2000)
{
    public string Level => MaxUs >= 2000 ? "RUIM" : MaxUs >= 500 ? "ATENÇÃO" : "OK";
    public string Line => $"{MaxUs:N0} µs no pior caso · {Count:N0} chamadas · {Over500:N0} acima de 0,5 ms";
}

public sealed record LatencyResult(DateTime When, double Seconds, double MaxUs, int Events, List<DriverLatency> Drivers, string Verdict, string? Error)
{
    public string Level => Error != null ? "ERRO" : MaxUs >= 2000 ? "RUIM" : MaxUs >= 500 ? "ATENÇÃO" : "OK";
}

/// <summary>
/// Teste de engasgos, no princípio do LatencyMon: escuta pelo ETW do kernel as DPCs e interrupções (ISRs), o tempo que
/// cada driver segura a CPU antes de devolvê-la ao jogo. Acima de 0,5 ms já pode aparecer como microengasgo e estalo no
/// áudio; acima de 2 ms é problema. O endereço de cada rotina é ligado ao driver pela lista de drivers carregados
/// (eventos de imagem do kernel), e cada driver conhecido vem com a correção mais comum.
/// </summary>
public static class LatencyTest
{
    public static LatencyResult Run(int seconds, Action<string> progress, CancellationToken ct)
    {
        var images = new List<(ulong Base, ulong End, string File)>();
        var stats = new Dictionary<string, (int N, double Max, int O500, int O2000)>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<(ulong Routine, double Us)>();
        int events = 0;
        object gate = new();

        void Count(ulong routine, double us)
        {
            events++;
            string? file = null;
            foreach (var im in images) if (routine >= im.Base && routine < im.End) { file = im.File; break; }
            if (file == null) { if (unresolved.Count < 200_000) unresolved.Add((routine, us)); return; }
            stats.TryGetValue(file, out var s);
            stats[file] = (s.N + 1, Math.Max(s.Max, us), s.O500 + (us >= 500 ? 1 : 0), s.O2000 + (us >= 2000 ? 1 : 0));
        }

        TraceEventSession session;
        try
        {
            session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.DeferedProcedureCalls
                                         | KernelTraceEventParser.Keywords.Interrupt
                                         | KernelTraceEventParser.Keywords.ImageLoad);
        }
        catch (Exception ex)
        {
            return new LatencyResult(DateTime.Now, 0, 0, 0, new(), "", $"O Windows não liberou a medição do kernel: {ex.Message}. " +
                                     "Feche outros monitores (LatencyMon, xperf, Process Hacker) e abra o tHg como administrador.");
        }

        using (session)
        {
            var source = session.Source;
            _ = source.Kernel; // registra o leitor de eventos do kernel
            source.AllEvents += e =>
            {
                string name = e.EventName ?? "";
                try
                {
                    if (name.StartsWith("Image/", StringComparison.Ordinal))
                    {
                        ulong b = Convert.ToUInt64(e.PayloadByName("ImageBase") ?? 0UL);
                        ulong size = Convert.ToUInt64(e.PayloadByName("ImageSize") ?? 0UL);
                        string f = Path.GetFileName(e.PayloadByName("FileName") as string ?? "");
                        if (b != 0 && size != 0 && f.Length > 0) lock (gate) images.Add((b, b + size, f));
                        return;
                    }
                    bool dpc = name.StartsWith("PerfInfo/", StringComparison.Ordinal) && name.Contains("DPC", StringComparison.Ordinal);
                    bool isr = name == "PerfInfo/ISR";
                    if (!dpc && !isr) return;
                    ulong routine = Convert.ToUInt64(e.PayloadByName("Routine") ?? 0UL);
                    double us = e.PayloadByName("InitialTime") is DateTime start ? (e.TimeStamp - start).TotalMilliseconds * 1000 : -1;
                    if (us < 0 || us > 1_000_000) return; // relógio fora de ordem: ignora
                    lock (gate) Count(routine, us);
                }
                catch { }
            };

            var pump = Task.Run(() => { try { source.Process(); } catch { } });
            for (int i = 0; i < seconds && !ct.IsCancellationRequested; i++)
            {
                progress($"Medindo… {seconds - i} s. Use o PC normalmente (ou deixe um jogo aberto) enquanto isso.");
                Thread.Sleep(1000);
            }
            try { session.Stop(); } catch { }
            pump.Wait(5000);
        }

        // Rotinas que vieram antes da lista de drivers chegar: liga agora.
        lock (gate)
        {
            var late = unresolved.ToList(); unresolved.Clear(); events -= late.Count;
            foreach (var (r, us) in late) Count(r, us);
        }

        var drivers = stats.Select(kv => { var (what, fix) = Explain(kv.Key); return new DriverLatency(kv.Key, what, fix, kv.Value.N, kv.Value.Max, kv.Value.O500, kv.Value.O2000); })
                           .OrderByDescending(d => d.MaxUs).ToList();
        double max = drivers.Count > 0 ? drivers[0].MaxUs : 0;
        string verdict = events == 0
            ? "Nenhum evento recebido. Outro programa pode estar usando a medição do kernel."
            : max >= 2000 ? $"Há driver segurando a CPU por {max / 1000:0.0} ms: isso aparece como engasgo em jogo e estalo no áudio. Comece pelo primeiro da lista."
            : max >= 500 ? "Latência um pouco alta em algum driver. Pode causar microengasgos em jogos competitivos; veja as sugestões."
            : "Latência baixa: os drivers não estão atrapalhando. Se o jogo engasga, a causa está em outro lugar (temperatura, memória, o próprio jogo).";
        return new LatencyResult(DateTime.Now, seconds, max, events, drivers.Take(15).ToList(), verdict, null);
    }

    /// <summary>O que é o driver e a correção mais comum (as mesmas recomendações de LatencyMon e fóruns de áudio/jogos).</summary>
    public static (string What, string Fix) Explain(string file)
    {
        string f = file.ToLowerInvariant();
        if (f is "ndis.sys" or "tcpip.sys" or "netio.sys" or "afd.sys" || f.StartsWith("rt6") || f.StartsWith("rt8") || f.StartsWith("rtux")
            || f.StartsWith("e1") || f.StartsWith("e2f") || f.StartsWith("ixgbe") || f.StartsWith("kill") || f.StartsWith("b57"))
            return ("Rede (placa de rede com cabo)", "Atualize o driver da placa de rede pelo site do fabricante da placa-mãe. Nas propriedades do adaptador, desligue \"Economia de energia\" / \"Green Ethernet\" e teste \"Moderação de interrupção\".");
        if (f.StartsWith("netwtw") || f.StartsWith("netwbw") || f.StartsWith("rtwlan") || f.StartsWith("mtkwl") || f.StartsWith("athw") || f.StartsWith("bcmwl") || f.StartsWith("qcwlan"))
            return ("Wi-Fi", "Drivers de Wi-Fi são a causa mais comum de latência alta. Atualize o driver; para jogar, prefira cabo e desligue o Wi-Fi quando não usar.");
        if (f is "nvlddmkm.sys" or "nvkflt.sys") return ("Placa de vídeo NVIDIA", "Atualize o driver (ou faça uma instalação limpa pela DDU) e ligue o modo MSI da placa no OTIMIZA.");
        if (f.StartsWith("amdkmdag") || f.StartsWith("atikmdag") || f.StartsWith("amdkmpfd") || f.StartsWith("amdfendr"))
            return ("Placa de vídeo AMD", "Atualize o driver (ou faça uma instalação limpa pela DDU) e ligue o modo MSI da placa no OTIMIZA.");
        if (f.StartsWith("igdkmd")) return ("Vídeo integrado Intel", "Atualize o driver de vídeo da Intel. Se usa placa dedicada, ligue o monitor nela.");
        if (f is "dxgkrnl.sys" or "dxgmms2.sys" or "watchdog.sys") return ("Vídeo (parte do Windows)", "Normalmente reflete o driver de vídeo: atualize-o e confira o modo MSI da placa.");
        if (f is "storport.sys" or "stornvme.sys" or "storahci.sys" or "iastorac.sys" or "iastora.sys" or "amdsata.sys" or "amdxata.sys" or "disk.sys" or "classpnp.sys")
            return ("Disco (SSD/HD)", "Atualize o firmware do SSD (app do fabricante) e o driver de chipset. Discos externos USB também aparecem aqui.");
        if (f is "acpi.sys" or "intelppm.sys" or "amdppm.sys" or "processr.sys")
            return ("Energia e BIOS", "Atualize a BIOS e o driver de chipset. Em notebooks, controles de bateria e ventoinha costumam causar isso; teste o plano de energia de alto desempenho.");
        if (f is "wdf01000.sys" or "usbxhci.sys" or "usbport.sys" or "ucx01000.sys" or "usbhub3.sys" or "hidusb.sys" or "hidclass.sys")
            return ("USB e dispositivos", "Desconecte um a um os dispositivos USB (hubs, controles, placas de captura, RGB) para achar o culpado; use portas da traseira da placa-mãe e atualize o driver de chipset.");
        if (f is "hdaudbus.sys" or "portcls.sys" or "ks.sys" or "ksthunk.sys" || f.StartsWith("rtkvhd") || f.StartsWith("rtkhda") || f.StartsWith("intcaudiobus") || f.StartsWith("ctaud"))
            return ("Áudio", "Atualize o driver de áudio e desligue \"aprimoramentos de áudio\" nas propriedades do dispositivo. Softwares de som (Nahimic, Sonic Studio) também pesam.");
        if (f is "mouclass.sys" or "kbdclass.sys" || f.StartsWith("mou") || f.StartsWith("kbd"))
            return ("Mouse e teclado", "Normal em mouses de 4000–8000 Hz. Se incomodar, use 1000 Hz no software do mouse.");
        if (f is "ntoskrnl.exe" or "hal.dll") return ("Núcleo do Windows", "Geralmente temporizadores do sistema; não costuma ser o culpado. Veja os outros itens da lista.");
        if (f.StartsWith("vgk") || f.StartsWith("easyanticheat") || f.StartsWith("bedaisy") || f.StartsWith("faceit"))
            return ("Anti-cheat", "Faz parte do jogo; atualize o jogo. Se a latência for alta só com ele carregado, vale reportar ao suporte do jogo.");
        return ("Driver de terceiros", "Procure o nome do arquivo na internet para saber de qual programa é e atualize ou desinstale esse programa.");
    }
}
