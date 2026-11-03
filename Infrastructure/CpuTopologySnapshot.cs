using System.Runtime.InteropServices;

namespace ThgOptimization.Infrastructure;

/// <summary>Um processador lógico como o Windows descreve em GetSystemCpuSetInformation.</summary>
/// <param name="CoreId">Identifica o núcleo físico: threads de SMT do mesmo núcleo têm o mesmo valor.</param>
/// <param name="CacheId">Identifica o último nível de cache: núcleos do mesmo CCD têm o mesmo valor.</param>
/// <param name="EfficiencyClass">Maior = mais desempenho. Igual em todos fora das CPUs híbridas.</param>
public readonly record struct LogicalCpu(uint CpuSetId, ushort Group, byte Index, int CoreId, int CacheId, byte EfficiencyClass);

/// <summary>Um domínio de último nível de cache e o tamanho informado pelo Windows.</summary>
public readonly record struct CacheDomain(int Id, ulong SizeBytes);

/// <summary>
/// Topologia real da CPU, lida do Windows e não deduzida do nome comercial. Cada CCD é um domínio de L3, que é o que o Windows expõe
/// de forma confiável. CPUID entra só como identificação do fabricante: ler a topologia por ele exigiria mover a thread por todos os núcleos.
/// </summary>
public sealed class CpuTopologySnapshot
{
    public IReadOnlyList<LogicalCpu> Cpus { get; }
    public IReadOnlyList<CacheDomain> Caches { get; }
    public string Vendor { get; }

    public CpuTopologySnapshot(IReadOnlyList<LogicalCpu> cpus, IReadOnlyList<CacheDomain> caches, string vendor = "")
    {
        Cpus = cpus;
        Caches = caches;
        Vendor = vendor;
    }

    public int LogicalCount => Cpus.Count;
    public int PhysicalCount => Cpus.Select(c => (c.Group, c.CoreId)).Distinct().Count();
    public bool HasSmt => LogicalCount > PhysicalCount;
    public int GroupCount => Cpus.Select(c => c.Group).Distinct().Count();
    public int CcdCount => Math.Max(1, Cpus.Select(c => (c.Group, c.CacheId)).Distinct().Count());
    public bool IsHybrid => Cpus.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
    /// <summary>Dois ou mais CCDs com L3 de tamanhos diferentes: é o que distingue um Ryzen X3D de dois CCDs.</summary>
    public bool HasAsymmetricCache => Caches.Count > 1 && Caches.Select(c => c.SizeBytes).Distinct().Count() > 1;

    public string Summary =>
        $"{PhysicalCount} núcleos físicos, {LogicalCount} threads, {CcdCount} CCD(s)" + (IsHybrid ? ", híbrida" : "") + (HasAsymmetricCache ? ", L3 assimétrico" : "") +
        (GroupCount > 1 ? $", {GroupCount} grupos" : "");

    private static readonly Lazy<CpuTopologySnapshot> _current = new(CpuTopologyReader.Read);
    /// <summary>Lida uma vez: a topologia não muda com o app aberto.</summary>
    public static CpuTopologySnapshot Current => _current.Value;
}

/// <summary>Monta a topologia a partir dos buffers do Windows. Os parsers recebem bytes para serem testados sem depender da CPU da máquina.</summary>
public static unsafe class CpuTopologyReader
{
    private const int RelationCache = 2, RelationAll = 0xFFFF, CpuSetInformation = 0, CacheUnified = 0, CacheData = 2;

    public static CpuTopologySnapshot Read()
    {
        try
        {
            byte[]? sets = Query(0, (byte* b, ref uint l) => GetSystemCpuSetInformation(b, l, ref l, IntPtr.Zero, 0));
            byte[]? glpi = Query(0, (byte* b, ref uint l) => GetLogicalProcessorInformationEx(RelationAll, b, ref l));
            var cpus = sets != null ? ParseCpuSets(sets) : new List<LogicalCpu>();
            var masks = glpi != null ? ParseL3(glpi) : new List<(ushort Group, ulong Mask, ulong Size)>();
            return Build(cpus, masks, ReadVendor());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            AppLog.Error("cpu", ex);
            return new CpuTopologySnapshot(Array.Empty<LogicalCpu>(), Array.Empty<CacheDomain>());
        }
    }

    private delegate bool Fill(byte* buffer, ref uint length);

    private static byte[]? Query(uint start, Fill fill)
    {
        uint len = start;
        fill(null, ref len);
        if (len == 0) return null;
        var buf = new byte[len];
        fixed (byte* p = buf)
            return fill(p, ref len) ? buf[..(int)len] : null;
    }

    /// <summary>SYSTEM_CPU_SET_INFORMATION: Size@0, Type@4, Id@8, Group@12, LogicalProcessorIndex@14, CoreIndex@15, LastLevelCacheIndex@16, EfficiencyClass@18.</summary>
    public static List<LogicalCpu> ParseCpuSets(ReadOnlySpan<byte> buf)
    {
        var list = new List<LogicalCpu>();
        for (int off = 0; off + 8 <= buf.Length;)
        {
            int size = BitConverter.ToInt32(buf[off..]);
            if (size <= 0 || off + size > buf.Length) break;
            if (BitConverter.ToInt32(buf[(off + 4)..]) == CpuSetInformation && size >= 19)
                list.Add(new LogicalCpu(BitConverter.ToUInt32(buf[(off + 8)..]), BitConverter.ToUInt16(buf[(off + 12)..]), buf[off + 14], buf[off + 15], buf[off + 16], buf[off + 18]));
            off += size;
        }
        return list;
    }

    /// <summary>Caches de nível 3: Relationship@0, Size@4, Level@8, CacheSize@12, Type@16, GroupMask@40 (Mask, Group@48).</summary>
    public static List<(ushort Group, ulong Mask, ulong Size)> ParseL3(ReadOnlySpan<byte> buf)
    {
        var list = new List<(ushort, ulong, ulong)>();
        for (int off = 0; off + 8 <= buf.Length;)
        {
            int size = BitConverter.ToInt32(buf[(off + 4)..]);
            if (size <= 0 || off + size > buf.Length) break;
            if (BitConverter.ToInt32(buf[off..]) == RelationCache && size >= 50 && buf[off + 8] == 3
                && BitConverter.ToInt32(buf[(off + 16)..]) is CacheUnified or CacheData)
                list.Add((BitConverter.ToUInt16(buf[(off + 48)..]), BitConverter.ToUInt64(buf[(off + 40)..]), BitConverter.ToUInt32(buf[(off + 12)..])));
            off += size;
        }
        return list;
    }

    /// <summary>Junta cada cache de L3 aos processadores lógicos da máscara dele, pelo LastLevelCacheIndex que o Windows já deu.</summary>
    public static CpuTopologySnapshot Build(IReadOnlyList<LogicalCpu> cpus, IReadOnlyList<(ushort Group, ulong Mask, ulong Size)> l3, string vendor = "")
    {
        var caches = new List<CacheDomain>();
        foreach (var id in cpus.Select(c => c.CacheId).Distinct())
        {
            ulong size = 0;
            foreach (var cpu in cpus)
            {
                if (cpu.CacheId != id || cpu.Index >= 64) continue;
                foreach (var cache in l3)
                    if (cache.Group == cpu.Group && (cache.Mask & (1UL << cpu.Index)) != 0) size = Math.Max(size, cache.Size);
            }
            caches.Add(new CacheDomain(id, size));
        }
        return new CpuTopologySnapshot(cpus, caches, vendor);
    }

    private static string ReadVendor()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return "";
        var (_, ebx, ecx, edx) = System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0);
        Span<int> parts = stackalloc[] { ebx, edx, ecx };
        return System.Text.Encoding.ASCII.GetString(MemoryMarshal.AsBytes(parts));
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemCpuSetInformation(byte* info, uint bufferLength, ref uint returnedLength, IntPtr process, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);
}

public enum CpuPlanKind
{
    /// <summary>Nada é definido: o escalonador do Windows decide.</summary>
    WindowsDefault,
    SingleCcd,
    SymmetricCcds,
    AsymmetricCache,
    Hybrid,
}

/// <summary>Quem fica onde durante o jogo. Lista vazia = nenhuma restrição para aquele grupo: o Windows decide.</summary>
/// <param name="GamePreferredCpuSets">Vazio não é "nenhum núcleo": o jogo nem é aberto e pode usar todos.</param>
/// <param name="BackgroundCpuSets">Onde ficam os programas de fundo. Nunca coincide com GamePreferredCpuSets.</param>
/// <param name="ReservedCpuSets">Fora do jogo por escolha do usuário (núcleo 0).</param>
public sealed record CpuSetPlan(CpuPlanKind Kind, IReadOnlyList<LogicalCpu> GamePreferredCpuSets, IReadOnlyList<LogicalCpu> BackgroundCpuSets, IReadOnlyList<LogicalCpu> ReservedCpuSets, string Reason)
{
    public bool RestrictsGame => GamePreferredCpuSets.Count > 0;
    public ulong GameMask => Mask(GamePreferredCpuSets);
    public ulong BackgroundMask => Mask(BackgroundCpuSets);

    private static ulong Mask(IReadOnlyList<LogicalCpu> cpus)
    {
        ulong mask = 0;
        foreach (var c in cpus) if (c.Index < 64) mask |= 1UL << c.Index;
        return mask;
    }
}

/// <param name="PreferFastCores">O usuário pediu o jogo nos núcleos mais rápidos (GameModeAffinity).</param>
/// <param name="UserAvoidCpu0">O usuário marcou a opção do núcleo 0. Nunca vem do perfil do jogo nem da contagem de threads.</param>
/// <param name="IsolateBackground">Programas de fundo separados do jogo (GameModeBgCores ou perfil automático).</param>
public readonly record struct CpuPlanOptions(bool PreferFastCores, bool UserAvoidCpu0, bool IsolateBackground);

/// <summary>
/// Decide os conjuntos de CPU a partir da topologia, sem olhar nome de CPU, fabricante ou jogo. Quando a separação não é segura,
/// a resposta é não fazer nada: o escalonador do Windows é melhor que uma restrição sem evidência.
/// </summary>
public static class CpuSetPlanner
{
    /// <summary>Abaixo disso o jogo perderia mais do que ganha: as restrições ao jogo são descartadas.</summary>
    public const int MinGameThreads = 4;
    /// <summary>Menos de 6 threads não tem o que separar.</summary>
    public const int MinThreadsToSeparate = 6;
    /// <summary>Com menos de 6 núcleos físicos, reservar um para o fundo tira 1/5 da capacidade do jogo.</summary>
    public const int MinCoresToIsolateOnSingleCcd = 6;

    private static readonly IReadOnlyList<LogicalCpu> None = Array.Empty<LogicalCpu>();

    public static CpuSetPlan Plan(CpuTopologySnapshot t, CpuPlanOptions o)
    {
        if (t.LogicalCount == 0) return Default("O Windows não informou a topologia da CPU.");
        if (t.GroupCount > 1) return Default("Mais de um grupo de processadores: fica com o escalonador do Windows.");
        if (t.LogicalCount < MinThreadsToSeparate) return Default($"{t.LogicalCount} threads não dão para separar com segurança.");

        var all = t.Cpus;
        var (kind, game, background, why) = Choose(t, o);

        IReadOnlyList<LogicalCpu> reserved = None;
        if (o.UserAvoidCpu0)
        {
            var first = all.OrderBy(c => c.Index).First();
            reserved = all.Where(c => c.CoreId == first.CoreId && c.CacheId == first.CacheId).ToList();
            var rest = (game.Count > 0 ? game : all).Except(reserved).Except(background).ToList();
            if (rest.Count >= MinGameThreads) { game = rest; why += " Núcleo 0 fora do jogo, por sua escolha."; }
            else reserved = None;
        }

        // Nunca os dois no mesmo núcleo físico, nem o fundo em tudo: aí a separação não existe e o fundo volta ao padrão.
        if (background.Count >= t.LogicalCount || (game.Count > 0 && background.Any(b => game.Any(g => g.CoreId == b.CoreId && g.CacheId == b.CacheId))))
            background = None;
        if (game.Count > 0 && game.Count < MinGameThreads) { game = None; why += " Sobrariam poucos núcleos: o jogo fica sem restrição."; }

        return new CpuSetPlan(kind, game, background, reserved, why);
    }

    private static (CpuPlanKind, IReadOnlyList<LogicalCpu> Game, IReadOnlyList<LogicalCpu> Background, string Why) Choose(CpuTopologySnapshot t, CpuPlanOptions o)
    {
        var all = t.Cpus;
        if (t.IsHybrid)
        {
            byte top = all.Max(c => c.EfficiencyClass);
            var fast = all.Where(c => c.EfficiencyClass == top).ToList();
            var slow = all.Where(c => c.EfficiencyClass != top).ToList();
            return (CpuPlanKind.Hybrid, o.PreferFastCores ? fast : None, o.IsolateBackground ? slow : None,
                    $"CPU híbrida: {fast.Count} threads de desempenho e {slow.Count} eficientes.");
        }
        if (t.HasAsymmetricCache)
        {
            var big = t.Caches.OrderByDescending(c => c.SizeBytes).First();
            var fast = all.Where(c => c.CacheId == big.Id).ToList();
            var others = all.Where(c => c.CacheId != big.Id).ToList();
            return (CpuPlanKind.AsymmetricCache, o.PreferFastCores ? fast : None, o.IsolateBackground ? others : None,
                    $"Dois CCDs com L3 diferente: jogo no de {big.SizeBytes >> 20} MB quando pedido, programas de fundo no outro.");
        }
        if (t.CcdCount > 1)
            return (CpuPlanKind.SymmetricCcds, None, o.IsolateBackground ? LastCore(all, all.Max(c => c.CacheId)) : None,
                    "CCDs iguais: nada indica qual rende mais, e o jogo pode usar todos.");

        bool isolate = o.IsolateBackground && t.PhysicalCount >= MinCoresToIsolateOnSingleCcd;
        return (CpuPlanKind.SingleCcd, None, isolate ? LastCore(all, all[0].CacheId) : None,
                "CCD único: o jogo pode usar todos os núcleos, sem restrição artificial.");
    }

    /// <summary>O último núcleo físico do domínio, com todos os threads dele.</summary>
    private static List<LogicalCpu> LastCore(IReadOnlyList<LogicalCpu> all, int cacheId)
    {
        var last = all.Where(c => c.CacheId == cacheId).OrderBy(c => c.Index).Last();
        return all.Where(c => c.CacheId == cacheId && c.CoreId == last.CoreId).ToList();
    }

    private static CpuSetPlan Default(string why) => new(CpuPlanKind.WindowsDefault, None, None, None, why);
}
