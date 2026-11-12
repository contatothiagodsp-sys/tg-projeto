namespace ThgOptimization.Core.Intelligence;

/// <summary>Um item do inventário: o que é, se o PC oferece e o valor lido. Ausente nunca é zero: tem estado e motivo.</summary>
public sealed record Capability(string Name, ReadingState State, string Value = "", string Note = "")
{
    public static Capability Of(string name, string? value, string unavailableNote = "não informado pelo Windows") =>
        string.IsNullOrWhiteSpace(value) || value.Trim() is "—" or "-" ? new Capability(name, ReadingState.Unavailable, "", unavailableNote)
                                                                        : new Capability(name, ReadingState.Supported, value.Trim());

    public static Capability Unsupported(string name, string why) => new(name, ReadingState.Unsupported, "", why);

    public string Display => State switch
    {
        ReadingState.Supported => Value,
        ReadingState.Unsupported => "não suportado" + (Note.Length > 0 ? $" ({Note})" : ""),
        ReadingState.Stale => Value + " (leitura antiga)",
        _ => "indisponível" + (Note.Length > 0 ? $" ({Note})" : ""),
    };
}

public sealed record CapabilityGroup(string Title, IReadOnlyList<Capability> Items);

/// <summary>
/// Perfil técnico do computador: o que existe, o que cada parte informa e o que o motor pode fazer aqui. Os campos tipados são os que
/// as regras usam; os grupos são o inventário completo para a tela.
/// </summary>
public sealed record MachineProfile(DateTime ScannedAt, string HardwareId, IReadOnlyList<CapabilityGroup> Groups)
{
    public string CpuName { get; init; } = "";
    public string GpuName { get; init; } = "";
    public string GpuVendor { get; init; } = "";
    public string? GpuDriver { get; init; }
    public int LogicalProcessors { get; init; }
    public int PhysicalCores { get; init; }
    public bool HasSmt { get; init; }
    /// <summary>Núcleos de desempenho e de eficiência (Intel híbrido).</summary>
    public bool IsHybridCpu { get; init; }
    /// <summary>Dois CCDs com cache diferente (Ryzen X3D de 12/16 núcleos).</summary>
    public bool HasAsymmetricCache { get; init; }
    public int ProcessorGroups { get; init; } = 1;
    public bool? IsLaptop { get; init; }
    public bool? OnBattery { get; init; }
    /// <summary>GPU integrada e dedicada ao mesmo tempo: a preferência por aplicativo importa.</summary>
    public bool HasIntegratedAndDedicatedGpu { get; init; }
    public double? RamGb { get; init; }
    public string? Resolution { get; init; }
    public int? RefreshHz { get; init; }
    public string? WindowsBuild { get; init; }
    /// <summary>O que o motor consegue medir neste PC: decide quais diagnósticos são possíveis.</summary>
    public IReadOnlyList<Capability> Sensors { get; init; } = Array.Empty<Capability>();

    /// <summary>Diagnósticos que este PC não sustenta, com o motivo, para a tela declarar em vez de esconder.</summary>
    public IReadOnlyList<string> Blind()
    {
        var list = new List<string>();
        foreach (var s in Sensors)
            if (s.State != ReadingState.Supported) list.Add($"{s.Name}: {s.Display}");
        return list;
    }

    /// <summary>Identidade do hardware para os baselines: CPU + GPU + RAM. Trocar uma peça começa uma referência nova.</summary>
    public static string MakeHardwareId(string cpu, string gpu, double? ramGb) =>
        string.Join(" + ", new[] { Clean(cpu), Clean(gpu), ramGb is double r ? $"{Math.Round(r):0} GB" : "" }.Where(x => x.Length > 0));

    private static string Clean(string s) => string.Join(' ', (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
