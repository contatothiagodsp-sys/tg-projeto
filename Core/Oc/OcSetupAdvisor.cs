using System.IO;
using System.Text.RegularExpressions;

namespace ThgOptimization.Core.Oc;

public enum CpuOcFamily { Unknown, AmdZen3, AmdZen4, AmdZen5, AmdOther, IntelUnlocked, IntelLocked, IntelCoreUltra }

/// <summary>O que este PC tem para ajuste fino. Só leitura: não carrega driver nem DLL.</summary>
public sealed record OcBackends(bool PawnIo, bool WinRing0, bool Nvml, bool NvApi, bool Adlx);

public sealed record OcVerdict(CpuOcFamily Family, string Cpu, string Gpu, string Backends);

/// <summary>
/// Análise do setup: diz o que vale a pena fazer neste hardware e o que fica na BIOS. Não aplica nada. A tabela é própria,
/// por família de CPU e GPU: o BuildCores OpenDB é CC BY-NC-SA, incompatível com embutir num app MIT sem uma decisão do dono.
/// </summary>
public static class OcSetupAdvisor
{
    private static readonly Regex Ryzen = new(@"Ryzen\s+(?:Threadripper\s+)?\d\s+(\d)(\d{3})(X3D|XT|X|F|G|GE|GT)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IntelCore = new(@"\bi[3579]-(\d{1,2})(\d{3})([A-Z]{0,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CoreUltra = new(@"Core\(?T?M?\)?\s+Ultra\s+\d\s+\d{3}([A-Z]{0,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static CpuOcFamily Family(string cpu)
    {
        cpu ??= "";
        var r = Ryzen.Match(cpu);
        if (r.Success)
        {
            // APUs (G, GE) têm outra caixa de mensagens no SMU: fora da tabela.
            if (r.Groups[3].Value.StartsWith("G", StringComparison.OrdinalIgnoreCase)) return CpuOcFamily.AmdOther;
            return r.Groups[1].Value switch { "5" => CpuOcFamily.AmdZen3, "7" => CpuOcFamily.AmdZen4, "9" => CpuOcFamily.AmdZen5, _ => CpuOcFamily.AmdOther };
        }
        if (cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return CpuOcFamily.AmdOther;
        var u = CoreUltra.Match(cpu);
        if (u.Success) return CpuOcFamily.IntelCoreUltra;
        var i = IntelCore.Match(cpu);
        if (i.Success) return i.Groups[3].Value.Contains('K', StringComparison.OrdinalIgnoreCase) ? CpuOcFamily.IntelUnlocked : CpuOcFamily.IntelLocked;
        return CpuOcFamily.Unknown;
    }

    /// <summary>Intel de 13ª e 14ª geração: instabilidade conhecida por tensão alta. Vale cuidado redobrado.</summary>
    internal static bool IsRaptorLakeRisk(string cpu)
    {
        var i = IntelCore.Match(cpu ?? "");
        return i.Success && i.Groups[1].Value is "13" or "14";
    }

    public static string CpuAdvice(string cpu)
    {
        var f = Family(cpu);
        string name = (cpu ?? "").Trim();
        bool x3d = name.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        return f switch
        {
            CpuOcFamily.AmdZen3 when x3d =>
                "Sem PBO completo na maioria das placas: priorize refrigeração e memória. Curve Optimizer só onde a BIOS oferece.",
            CpuOcFamily.AmdZen3 or CpuOcFamily.AmdZen4 or CpuOcFamily.AmdZen5 =>
                (WindowsTweaks.IsDualCcdX3D(name) ? "Dois CCDs: mantenha o plano Equilibrado e o driver de chipset em dia. " : "") +
                $"Caminho recomendado: PBO + Curve Optimizer negativo, na BIOS. Teto de referência: {OcLimits.AmdCpuMaxVolts:0.00} V e {OcLimits.AmdCpuMaxTempC:0} °C.",
            CpuOcFamily.AmdOther => "Modelo fora da tabela de ajuste fino: use só os limites da BIOS.",
            CpuOcFamily.IntelUnlocked when IsRaptorLakeRisk(name) =>
                $"13ª/14ª geração: atualize a BIOS com o microcódigo mais novo antes de tudo e prefira undervolt a subir clock. Teto: {OcLimits.IntelCpuMaxVolts:0.00} V e {OcLimits.IntelCpuMaxTempC:0} °C.",
            CpuOcFamily.IntelUnlocked =>
                $"Prefira undervolt e limites de energia (PL1/PL2) na BIOS. Teto: {OcLimits.IntelCpuMaxVolts:0.00} V e {OcLimits.IntelCpuMaxTempC:0} °C.",
            CpuOcFamily.IntelLocked => "Multiplicador travado: o ganho vem da memória e da refrigeração. Undervolt depende da placa e costuma estar bloqueado.",
            CpuOcFamily.IntelCoreUltra => "Core Ultra: use o perfil de energia da placa e a memória. O ajuste fino por software não foi validado nesta família.",
            _ => "CPU não identificada: nenhuma recomendação específica.",
        };
    }

    public static string GpuAdvice(string gpu)
    {
        string g = gpu ?? "";
        if (g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || g.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
            return $"NVIDIA: limite de energia e teto de clock já funcionam nesta página. Referência para offset: até +{OcLimits.NvidiaMaxOffsetMhz} MHz e {OcLimits.NvidiaMaxTempC:0} °C.";
        if (g.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || g.StartsWith("AMD", StringComparison.OrdinalIgnoreCase))
            return $"AMD: ajuste pelo painel do Adrenalin (Desempenho → Ajuste). Referência: até +{OcLimits.AmdGpuMaxOffsetPercent}% e {OcLimits.AmdGpuMaxTempC:0} °C.";
        return "GPU sem ajuste por software nesta versão.";
    }

    public static string BackendSummary(OcBackends b)
    {
        var parts = new List<string> { b.PawnIo ? "PawnIO instalado" : "PawnIO ausente" };
        if (b.WinRing0) parts.Add("WinRing0 encontrado (driver inseguro: remova o programa que o instalou)");
        if (b.Nvml) parts.Add("NVML");
        if (b.NvApi) parts.Add("NVAPI");
        if (b.Adlx) parts.Add("ADLX");
        return string.Join(" · ", parts);
    }

    /// <summary>Procura só por arquivos e chaves do registro: nada é carregado.</summary>
    public static OcBackends ProbeBackends()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        bool Has(string dll) { try { return File.Exists(Path.Combine(sys, dll)); } catch { return false; } }
        bool ring0 = false;
        try
        {
            foreach (var s in SecurityAudit.WinRing0Services)
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{s}");
                ring0 |= k != null;
            }
        }
        catch { }
        return new OcBackends(SensorMonitor.IsPawnIOInstalled, ring0, Has("nvml.dll"), Has("nvapi64.dll"), Has("amdadlx64.dll"));
    }

    public static OcVerdict Analyze(string cpu, string gpu, OcBackends backends) =>
        new(Family(cpu), CpuAdvice(cpu), GpuAdvice(gpu), BackendSummary(backends));
}
