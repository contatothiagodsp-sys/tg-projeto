using System.Management;
using System.Text.RegularExpressions;

namespace ThgOptimization.Core.Performance;

/// <summary>Em que faixa uma CPU está, pelo nome. Nome desconhecido fica como moderna: sem dados, nenhum aviso de CPU antiga.</summary>
public static class CpuTier
{
    private static readonly Regex IntelCore = new(@"\bi[3579]-(\d{4,5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Ryzen = new(@"\bRyzen\s+(?:Threadripper\s+)?(?:PRO\s+)?(?:\d\s+)?(?:PRO\s+)?(\d)\d{3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Entry = new(@"\b(i3-|Ryzen\s+3\b|Pentium|Celeron|Athlon|FX-)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Geração do Core i: 8700 é a 8ª, 12700 é a 12ª. Null = não é Core i.</summary>
    internal static int? IntelGeneration(string cpu)
    {
        var m = IntelCore.Match(cpu);
        if (!m.Success) return null;
        string digits = m.Groups[1].Value;
        return int.Parse(digits.Length == 4 ? digits[..1] : digits[..2]);
    }

    /// <summary>Antes do Intel 11ª geração ou do Ryzen 2000: a faixa em que a Microsoft diz que a virtualização de segurança pesa mais.</summary>
    public static bool IsOld(string cpu)
    {
        if (IntelGeneration(cpu) is int gen) return gen < 11;
        var ryzen = Ryzen.Match(cpu);
        if (ryzen.Success) return ryzen.Groups[1].Value == "1";
        return Regex.IsMatch(cpu, @"\b(Pentium|Celeron|FX-)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsEntry(string cpu) => Entry.IsMatch(cpu);
}

/// <summary>
/// Quanto a Integridade de Memória (HVCI) deve custar neste PC. É uma estimativa pela geração da CPU e pela VRAM: o número real varia por jogo e só sai
/// medindo antes e depois. O tHg nunca desliga a proteção: só informa, abre a tela do Windows e mede se o usuário quiser.
/// </summary>
public static class SystemSecurityAdvisor
{
    public sealed record SecurityImpact(double? EstimatedFpsLossPct, bool IsHighRisk, string Explanation);

    private const double VramEntryGb = 8;
    private const string Basis = "Estimativa pela geração da CPU e pela VRAM, sem medir este PC. O número real varia por jogo: use MEDIR IMPACTO para saber.";

    public static SecurityImpact Estimate(GpuInfo? gpu, string cpuName, bool hvciOn)
    {
        if (!hvciOn) return new SecurityImpact(null, false, "A Integridade de Memória está desligada: sem esse custo, e sem essa proteção contra drivers maliciosos e rootkits.");
        if (CpuTier.IsOld(cpuName))
            return new SecurityImpact(12, true, "Nesta CPU parte da proteção é emulada, o que pesa mais em jogos limitados pela CPU: faixa de 10 a 15%. " + Basis);
        double? vramGb = gpu is { VramBytes: > 0 } ? gpu.VramBytes / (double)(1UL << 30) : null;
        if (vramGb < VramEntryGb)
            return new SecurityImpact(3.5, false, "CPU moderna com placa de entrada: faixa de 2 a 5%. " + Basis);
        return new SecurityImpact(1.5, false, "CPU moderna com placa intermediária ou forte: abaixo de 2% na maioria dos jogos. " + Basis);
    }

    /// <summary>Lê o estado no Windows. WMI: fora da thread da janela.</summary>
    public static (bool VbsOn, bool HvciOn) ReadStatus()
    {
        int vbs = 0;
        bool hvci = false;
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard"));
        using var results = searcher.Get();
        foreach (ManagementObject o in results)
            using (o)
            {
                vbs = Convert.ToInt32(o["VirtualizationBasedSecurityStatus"] ?? 0);
                hvci = o["SecurityServicesRunning"] is uint[] running && running.Contains(2u);
            }
        return (vbs == 2, hvci);
    }

    /// <summary>Item do TURBO. Peso 0: é uma escolha do usuário entre proteção e desempenho, e não entra na nota.</summary>
    public static GamerCheck BuildCheck(GpuInfo? gpu, string cpuName, bool vbsOn, bool hvciOn)
    {
        var impact = Estimate(gpu, cpuName, hvciOn);
        return new GamerCheck
        {
            Id = "sec.hvci.impact", Area = "WINDOWS", Title = "Integridade de Memória — impacto estimado no FPS", Weight = 0, Level = CheckLevel.Info,
            Found = hvciOn ? $"Ligada · perda estimada de {impact.EstimatedFpsLossPct:0.#}% em jogos limitados pela CPU"
                  : vbsOn ? "VBS ligado, Integridade de Memória desligada"
                  : "Desligada · sem esse custo de FPS, e sem proteção contra rootkits",
            Advice = impact.Explanation + " O tHg não desliga proteções: se quiser trocar segurança por FPS, a decisão é sua.",
            Fix = FixKind.Settings, FixLabel = "VER NO WINDOWS", FixTarget = "windowsdefender://coreisolation",
            SecondaryLabel = hvciOn ? "MEDIR IMPACTO" : "",
        };
    }
}

public enum MeasurementStep { None, RemindOnce, RunBenchmark }

/// <summary>O "depois" da medição de um ajuste que pede reinício: só roda se o Windows realmente mudou, e nunca sem o usuário ver.</summary>
public static class MeasurementFlow
{
    /// <summary>Validade do benchmark usado como "antes": depois disso, driver e Windows já podem ter mudado.</summary>
    public static readonly TimeSpan BaselineMaxAge = TimeSpan.FromDays(30);

    public static MeasurementStep Decide(PendingMeasurement? pending, bool stateNow)
    {
        if (pending == null) return MeasurementStep.None;
        if (stateNow != pending.StateAtStart) return MeasurementStep.RunBenchmark;
        return pending.Reminded ? MeasurementStep.None : MeasurementStep.RemindOnce;
    }

    /// <summary>Benchmark de GPU mais recente que serve de "antes". Null = rode um primeiro.</summary>
    public static BenchRecord? Baseline(IEnumerable<BenchRecord> records, DateTime now) =>
        records.Where(r => (r.Test.StartsWith("rt:", StringComparison.Ordinal) || r.Test == "gpu") && r.Score > 0 && now - r.Date <= BaselineMaxAge)
               .OrderByDescending(r => r.Date).FirstOrDefault();

    /// <summary>Quanto o resultado mudou em relação ao "antes", em %. Positivo = o "depois" foi melhor.</summary>
    public static double DeltaPct(double before, double after) => before > 0 ? (after - before) / before * 100 : 0;

    public static string Message(string kind, double delta, bool stateNow) => kind switch
    {
        "hvci" when delta >= 1 => $"Medido neste PC: a Integridade de Memória custava {delta:0.#}% neste benchmark. Para religar, abra Segurança do Windows · Isolamento de núcleo.",
        "hvci" => $"Medido neste PC: sem custo mensurável da Integridade de Memória neste benchmark ({delta:+0.#;-0.#;0}%). Vale religar em Segurança do Windows · Isolamento de núcleo.",
        "hags" => $"Medido neste PC: com o agendamento de GPU {(stateNow ? "ligado" : "desligado")}, o benchmark mudou {delta:+0.#;-0.#;0}%. O ajuste pode ser desfeito em DESFAZER TUDO.",
        _ => $"Medido neste PC: o benchmark mudou {delta:+0.#;-0.#;0}%.",
    };
}
