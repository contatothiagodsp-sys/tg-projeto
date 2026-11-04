using System.Text.RegularExpressions;

namespace ThgOptimization.Core.Performance;

/// <summary>
/// O que mexer na BIOS para a memória, por plataforma. Weight 0 = nos X3D o ganho costuma ser pequeno, e o item não pesa na nota. Nada é aplicado pelo tHg:
/// cada passo é manual, e a estabilidade é conferida no stress de RAM depois de cada mudança.
/// </summary>
public static class RamTuningGuide
{
    public sealed record Item(string Label, string Value, string Why);
    public sealed record Advice(IReadOnlyList<Item> Items, int Weight, string Platform)
    {
        public string Text => $"Plataforma: {Platform}\n\n" + string.Join("\n\n", Items.Select(i => $"{i.Label}: {i.Value}\n{i.Why}")) +
                              "\n\nDepois de cada mudança, rode VALIDAR EXPO/XMP na página de stress. Se o PC não ligar, limpe o CMOS: a BIOS volta ao padrão.";
    }

    /// <param name="ramType">"DDR4" ou "DDR5"; vazio = desconhecido.</param>
    /// <param name="configuredMts">Velocidade em uso, em MT/s; 0 = desconhecida.</param>
    public static Advice For(string cpuName, string ramType, int configuredMts)
    {
        bool x3d = cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        bool ddr4 = ramType.Equals("DDR4", StringComparison.OrdinalIgnoreCase);
        if (cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            var items = ddr4 ? Am4() : Am5(configuredMts);
            if (x3d) items.Insert(0, new("Ganho esperado neste processador", "cerca de 1%", "O 3D V-Cache esconde boa parte da latência da RAM. Ligue o EXPO/XMP e pare por aí, a não ser que queira testar."));
            return new Advice(items, x3d ? 0 : 3, ddr4 ? "AM4" : "AM5");
        }
        if (cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase) || cpuName.Contains("Core", StringComparison.OrdinalIgnoreCase))
            return new Advice(Intel(configuredMts), 3, "Intel");
        return new Advice(new List<Item> { new("Perfil de fábrica", "XMP/EXPO ligado", "É o ganho seguro. Sem ele, a memória roda abaixo da velocidade da etiqueta.") }, 1, "não identificada");
    }

    private static List<Item> Am5(int mts) => new()
    {
        new("Perfil de fábrica", "EXPO ligado", "É o ganho seguro: sem ele, a memória roda na velocidade básica."),
        new("FCLK", "2000 MHz", "A malha entre os núcleos e a memória. 2000 é o valor comum no AM5; se aparecer erro, tente 1900."),
        new("UCLK = MEMCLK", "modo 1:1", mts > 6400 ? "Acima de 6400 MT/s muitas CPUs não aguentam o 1:1. Se não ficar estável, volte para 6000 MT/s, que costuma render mais do que uma velocidade alta em 2:1." : "Controlador e memória no mesmo clock: é o que mais baixa a latência."),
        new("tCL, tRCD, tRP, tRAS", "os do perfil primeiro; reduza um por vez", "Os números mais baixos da etiqueta são o ponto de partida. Mude um valor, valide e só então mude o próximo."),
        new("VDDIO / VSOC", "não passe de 1,40 V e 1,30 V", "Acima disso há risco de dano à CPU no AM5. O perfil EXPO costuma respeitar: confira depois de ligar."),
    };

    private static List<Item> Am4() => new()
    {
        new("Perfil de fábrica", "XMP/DOCP ligado", "É o ganho seguro: sem ele, a memória roda na velocidade básica."),
        new("FCLK", "1800 MHz (até 1900)", "Mantenha FCLK = UCLK = MEMCLK. Acima de 1900 costuma entrar em 2:1, que piora a latência."),
        new("Velocidade", "3600 MT/s", "É o ponto que costuma render melhor no AM4."),
        new("tCL, tRCD, tRP, tRAS", "os do perfil primeiro; reduza um por vez", "Mude um valor, valide e só então mude o próximo."),
    };

    private static List<Item> Intel(int mts) => new()
    {
        new("Perfil de fábrica", "XMP ligado", "É o ganho seguro: sem ele, a memória roda na velocidade básica."),
        new("Gear", mts >= 7200 ? "Gear 2" : "Gear 2 no DDR5; Gear 1 só em DDR4", mts >= 7200 ? "Em 7200 MT/s ou mais, o controlador não acompanha em Gear 1." : "Gear 1 tem latência menor, mas o DDR5 da 12ª à 14ª geração roda em Gear 2 na prática."),
        new("tCL, tRCD, tRP, tRAS", "os do perfil primeiro; reduza um por vez", "Mude um valor, valide e só então mude o próximo."),
        new("VDD2 / VDDQ", "o do perfil XMP", "Suba pouco de cada vez, e só se o stress apontar erro."),
    };

    /// <summary>Resultado do VALIDAR EXPO/XMP. Sem passadas completas, não há o que afirmar.</summary>
    public static string ValidationMessage(long memoryErrors, long passes, TimeSpan duration, bool stoppedByUser)
    {
        if (memoryErrors > 0)
            return $"{memoryErrors} erro(s) de memória em {duration.TotalMinutes:0} min: NÃO está estável. Volte um passo na BIOS. Erro de memória corrompe arquivos sem avisar.";
        if (passes == 0) return "O teste terminou antes de completar uma passada: não dá para concluir nada. Rode de novo.";
        string verdict = $"Nenhum erro em {passes} passada(s) e {duration.TotalMinutes:0} min";
        return stoppedByUser
            ? verdict + ". O teste foi interrompido antes dos 15 min: rode inteiro antes de confiar no ajuste."
            : verdict + ". Passar neste teste não garante estabilidade total, mas é um bom sinal.";
    }
}
