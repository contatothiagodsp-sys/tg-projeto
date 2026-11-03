using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ThgOptimization.Infrastructure;
using Spi = ThgOptimization.Infrastructure.ProcessUtil.SYSTEM_PROCESS_INFORMATION;

namespace ThgOptimization.Tests;

public class ProcessSnapshotLayoutTests
{
    private static int Offset(string field) => (int)Marshal.OffsetOf<Spi>(field);

    [Fact]
    public void Offsets_iguais_em_x64_e_x86()
    {
        Assert.Equal(0, Offset(nameof(Spi.NextEntryOffset)));
        Assert.Equal(32, Offset(nameof(Spi.CreateTime)));
        Assert.Equal(40, Offset(nameof(Spi.UserTime)));
        Assert.Equal(48, Offset(nameof(Spi.KernelTime)));
        Assert.Equal(56, Offset(nameof(Spi.ImageName)));
    }

    [Fact]
    public void Offsets_que_dependem_da_arquitetura()
    {
        Assert.Equal(IntPtr.Size == 8 ? 72 : 64, Offset(nameof(Spi.BasePriority)));
        Assert.Equal(IntPtr.Size == 8 ? 80 : 68, Offset(nameof(Spi.UniqueProcessId)));
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, (int)Marshal.OffsetOf<ProcessUtil.UNICODE_STRING>(nameof(ProcessUtil.UNICODE_STRING.Buffer)));
    }
}

/// <summary>Buffers forjados: o endereço-base é fictício porque o parser nunca desreferencia ponteiros do kernel.</summary>
public class ProcessSnapshotParseTests
{
    private const long Base = 0x10000;
    private static readonly int Header = Unsafe.SizeOf<Spi>();

    private static void Put(byte[] buf, int at, uint next, int pid, long user, long kernel, int nameAt, ushort nameBytes)
    {
        var e = new Spi
        {
            NextEntryOffset = next, UserTime = user, KernelTime = kernel, UniqueProcessId = pid,
            ImageName = new ProcessUtil.UNICODE_STRING { Length = nameBytes, MaximumLength = nameBytes, Buffer = nameAt < 0 ? 0 : (nint)(Base + nameAt) },
        };
        MemoryMarshal.Write(buf.AsSpan(at), in e);
    }

    private static int PutName(byte[] buf, int at, string name)
    {
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(buf.AsSpan(at));
        return name.Length * 2;
    }

    private static List<ProcessSnapshot> Parse(byte[] buf, int? length = null)
    {
        var list = new List<ProcessSnapshot>();
        ProcessUtil.Parse(buf, length ?? buf.Length, (nint)Base, list);
        return list;
    }

    [Fact]
    public void Le_dois_blocos_e_soma_kernel_com_usuario()
    {
        var buf = new byte[1024];
        int n1 = PutName(buf, 500, "app.exe"), n2 = PutName(buf, 600, "outro");
        Put(buf, 0, 200, 1234, user: 7, kernel: 3, 500, (ushort)n1);
        Put(buf, 200, 0, 5678, user: 1, kernel: 1, 600, (ushort)n2);
        Assert.Equal(new[] { new ProcessSnapshot(1234, "app", 10), new ProcessSnapshot(5678, "outro", 2) }, Parse(buf));
    }

    [Theory]
    [InlineData("foo.EXE", "foo")]
    [InlineData("foo.scr", "foo.scr")]
    [InlineData("Battle.net.exe", "Battle.net")]
    [InlineData(@"C:\Jogos\jogo.exe", "jogo")]
    public void Nome_igual_ao_de_ProcessName(string image, string expected)
    {
        var buf = new byte[512];
        int n = PutName(buf, 200, image);
        Put(buf, 0, 0, 100, 0, 0, 200, (ushort)n);
        Assert.Equal(expected, Assert.Single(Parse(buf)).Name);
    }

    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(4, "System")]
    [InlineData(321, "321")]
    public void Sem_nome_usa_o_mesmo_padrao_do_dotnet(int pid, string expected)
    {
        var buf = new byte[256];
        Put(buf, 0, 0, pid, 0, 0, -1, 0);
        Assert.Equal(expected, Assert.Single(Parse(buf)).Name);
    }

    [Theory]
    [InlineData(-10_000, 10)] // aponta antes do buffer
    [InlineData(250, 100)]    // termina depois do fim
    [InlineData(100, 7)]      // tamanho ímpar
    public void Nome_invalido_descarta_a_entrada_sem_ler_fora(int nameAt, int bytes)
    {
        var buf = new byte[256];
        var e = new Spi
        {
            UniqueProcessId = 100,
            ImageName = new ProcessUtil.UNICODE_STRING { Length = (ushort)bytes, Buffer = (nint)(Base + nameAt) },
        };
        MemoryMarshal.Write(buf.AsSpan(), in e);
        Assert.Empty(Parse(buf));
    }

    [Fact]
    public void NextEntryOffset_menor_que_o_cabecalho_nao_prende_o_laco()
    {
        var buf = new byte[512];
        Put(buf, 0, 1, 100, 0, 0, -1, 0); // sem a checagem, voltaria a ler bytes do mesmo bloco
        Assert.Single(Parse(buf));
    }

    [Fact]
    public void NextEntryOffset_gigante_para_no_fim_do_buffer()
    {
        var buf = new byte[512];
        Put(buf, 0, uint.MaxValue, 100, 0, 0, -1, 0);
        Assert.Single(Parse(buf));
    }

    [Fact]
    public void Bloco_cortado_no_fim_nao_e_lido()
    {
        var buf = new byte[Header * 2];
        Put(buf, 0, (uint)Header, 100, 0, 0, -1, 0);
        Put(buf, Header, 0, 200, 0, 0, -1, 0);
        Assert.Single(Parse(buf, Header * 2 - 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public void Tamanho_fora_do_buffer_nao_lanca(int length)
    {
        var buf = new byte[64];
        var ex = Record.Exception(() => Parse(buf, length));
        Assert.Null(ex);
    }
}

public class ProcessSnapshotRealTests
{
    [Fact]
    public void Nomes_iguais_aos_de_GetProcesses()
    {
        var snapshot = ProcessUtil.Snapshot().ToDictionary(s => s.Pid, s => s.Name);
        Assert.Contains(Environment.ProcessId, snapshot.Keys);
        int compared = 0;
        foreach (var p in Process.GetProcesses())
            using (p)
                if (snapshot.TryGetValue(p.Id, out var name)) { Assert.Equal(p.ProcessName, name); compared++; }
        Assert.True(compared > 10, $"só {compared} processos em comum");
    }

    [Fact]
    public void Tempo_de_cpu_do_proprio_processo_bate_com_TotalProcessorTime()
    {
        long snap = ProcessUtil.Snapshot().First(s => s.Pid == Environment.ProcessId).TotalTime100ns;
        using var self = Process.GetCurrentProcess();
        TimeSpan real = self.TotalProcessorTime; // lido depois: só pode ser igual ou maior
        TimeSpan fromSnap = TimeSpan.FromTicks(snap);
        Assert.True(fromSnap <= real + TimeSpan.FromSeconds(1) && real - fromSnap < TimeSpan.FromSeconds(30), $"snapshot {fromSnap}, real {real}");
    }

    [Fact]
    public void Duas_threads_ao_mesmo_tempo_nao_se_atrapalham()
    {
        var all = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8).Select(_ => ProcessUtil.Snapshot().Count).ToList();
        Assert.All(all, count => Assert.True(count > 10));
    }
}

/// <summary>
/// Nenhum teste aqui mexe em programas do usuário: alvos reais só passam com protect = sempre protegido (a sessão termina
/// antes de qualquer mudança) e o que é aplicado de verdade é num processo-filho criado pelo próprio teste.
/// </summary>
public class BackgroundSamplersTests
{
    private const int NotAGame = -1;

    private static int CountWin32(Action action) => FirstChance.CountWin32(action);

    /// <summary>Quanto de CPU (100 ns) uma leitura precisa somar em 3 s para passar com folga dos 10% da máquina.</summary>
    private static long Busy => (long)(Environment.ProcessorCount * 3 * 0.5 * TimeSpan.TicksPerSecond);

    /// <summary>Três leituras com CPU alta: a terceira é a que atinge 2 strikes.</summary>
    private static void DriveToLimit(BackgroundBalancer b, Func<int, List<ProcessSnapshot>> snapshotAt, Func<Process, bool>? protect)
    {
        b.Evaluate(snapshotAt(0), NotAGame, 0, protect);
        b.Evaluate(snapshotAt(1), NotAGame, 3, protect);
        b.Evaluate(snapshotAt(2), NotAGame, 3, protect);
    }

    private static List<ProcessSnapshot> One(int pid, string name, int step) => new() { new ProcessSnapshot(pid, name, step * Busy) };

    [Fact]
    public void Balancer_com_todos_os_processos_reais_no_limite_nao_lanca_Win32Exception()
    {
        // Pior caso possível: toda a lista real "passa de 10%", inclusive protegidos e serviços. Nada é alterado (protect = true).
        var real = ProcessUtil.Snapshot();
        Assert.NotEmpty(real);
        var balancer = new BackgroundBalancer();
        List<ProcessSnapshot> At(int step) => real.Select(s => s with { TotalTime100ns = s.TotalTime100ns + step * Busy }).ToList();
        Assert.Equal(0, CountWin32(() => DriveToLimit(balancer, At, _ => true)));
        Assert.Equal(0, balancer.RestoreAll());
    }

    [Fact]
    public void EcoQos_e_Cores_com_a_lista_real_nao_lancam_Win32Exception()
    {
        var eco = new BackgroundEcoQos();
        var cores = new BackgroundCores(new AppSettings());
        Assert.Equal(0, CountWin32(() =>
        {
            Assert.Equal(0, eco.Sample(NotAGame, _ => true, force: true));
            Assert.Equal(0, cores.Sample(NotAGame, _ => true, force: true));
        }));
    }

    [Fact]
    public void Balancer_nao_abre_quem_nao_atingiu_o_limite()
    {
        var balancer = new BackgroundBalancer();
        int opened = 0;
        balancer.Open = pid => { opened++; return Process.GetProcessById(pid); };
        var idle = ProcessUtil.Snapshot();
        balancer.Evaluate(idle, NotAGame, 0, null);
        balancer.Evaluate(idle, NotAGame, 3, null); // mesmos tempos: 0% de CPU
        balancer.Evaluate(idle, NotAGame, 3, null);
        Assert.Equal(0, opened);
    }

    [Fact]
    public void Balancer_acesso_negado_nao_lanca_nem_tenta_abrir()
    {
        // csrss é protegido pelo Windows, mesmo como administrador. O nome é fictício para passar da lista Never.
        var csrss = Process.GetProcessesByName("csrss");
        int pid = csrss[0].Id;
        foreach (var p in csrss) p.Dispose();
        var balancer = new BackgroundBalancer();
        int opened = 0;
        balancer.Open = id => { opened++; return Process.GetProcessById(id); };
        Assert.Equal(0, CountWin32(() => DriveToLimit(balancer, s => One(pid, "fake", s), _ => true)));
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData("VALORANT-Win64-Shipping")]
    [InlineData("VALORANT")]
    [InlineData("vgc")]
    [InlineData("vgtray")]
    [InlineData("vgm")]
    public void Vanguard_nunca_e_aberto(string name)
    {
        using var child = new Child();
        var balancer = new BackgroundBalancer();
        int opened = 0;
        balancer.Open = id => { opened++; return Process.GetProcessById(id); };
        DriveToLimit(balancer, s => One(child.Id, name, s), null);
        Assert.Equal(0, opened);
        Assert.Equal(ProcessPriorityClass.Normal, child.Priority);
    }

    [Fact]
    public void Balancer_rebaixa_no_limite_e_restaura()
    {
        using var child = new Child();
        var balancer = new BackgroundBalancer();
        balancer.Evaluate(One(child.Id, "fake", 0), NotAGame, 0, null);
        balancer.Evaluate(One(child.Id, "fake", 1), NotAGame, 3, null);
        Assert.Equal(ProcessPriorityClass.Normal, child.Priority); // 1 strike ainda não basta
        balancer.Evaluate(One(child.Id, "fake", 2), NotAGame, 3, null);
        Assert.Equal(ProcessPriorityClass.BelowNormal, child.Priority);
        Assert.Equal(1, balancer.RestoreAll());
        Assert.Equal(ProcessPriorityClass.Normal, child.Priority);
    }

    [Fact]
    public void Balancer_processo_protegido_e_avaliado_uma_vez_so()
    {
        using var child = new Child();
        var balancer = new BackgroundBalancer();
        int opened = 0;
        balancer.Open = id => { opened++; return Process.GetProcessById(id); };
        DriveToLimit(balancer, s => One(child.Id, "fake", s), _ => true);
        balancer.Evaluate(One(child.Id, "fake", 3), NotAGame, 3, _ => true);
        balancer.Evaluate(One(child.Id, "fake", 4), NotAGame, 3, _ => true);
        Assert.Equal(1, opened);
        Assert.Equal(ProcessPriorityClass.Normal, child.Priority);
    }

    [Fact]
    public void Balancer_PID_que_sumiu_e_voltou_pode_ser_avaliado_de_novo()
    {
        using var child = new Child();
        var balancer = new BackgroundBalancer();
        int opened = 0;
        balancer.Open = id => { opened++; return Process.GetProcessById(id); };
        DriveToLimit(balancer, s => One(child.Id, "fake", s), _ => true);
        balancer.Evaluate(One(child.Id + 4, "outro", 0), NotAGame, 3, _ => true); // o PID saiu da lista
        DriveToLimit(balancer, s => One(child.Id, "fake", s), _ => true);
        Assert.Equal(2, opened);
    }

    [Fact]
    public void EcoQos_so_abre_alvos_e_aplica_uma_vez()
    {
        using var child = new Child();
        var eco = new BackgroundEcoQos();
        int opened = 0;
        eco.Open = id => { opened++; return Process.GetProcessById(id); };

        Assert.Equal(0, eco.Apply(One(child.Id, "programa-qualquer", 0), NotAGame, null));
        Assert.Equal(0, opened);

        Assert.Equal(1, eco.Apply(One(child.Id, "chrome", 0), NotAGame, null));
        Assert.Equal(0, eco.Apply(One(child.Id, "chrome", 0), NotAGame, null));
        Assert.Equal(1, opened);
        Assert.Equal(1, eco.RestoreAll());
    }

    [Fact]
    public void EcoQos_nao_aplica_no_jogo_nem_em_quem_esta_protegido()
    {
        using var child = new Child();
        var eco = new BackgroundEcoQos();
        Assert.Equal(0, eco.Apply(One(child.Id, "chrome", 0), child.Id, null));
        Assert.Equal(0, eco.Apply(One(child.Id, "chrome", 0), NotAGame, _ => true));
        Assert.Equal(0, eco.RestoreAll());
    }

    [Fact]
    public void Cores_so_abre_alvos_e_respeita_protect()
    {
        // Sem aplicar de verdade: um alvo aplicado gravaria as configurações reais do usuário.
        using var child = new Child();
        var cores = new BackgroundCores(new AppSettings());
        int opened = 0;
        cores.Open = id => { opened++; return Process.GetProcessById(id); };

        Assert.Equal(0, cores.Apply(One(child.Id, "programa-qualquer", 0), NotAGame, 0b11, ulong.MaxValue, null));
        Assert.Equal(0, opened);

        Assert.Equal(0, cores.Apply(One(child.Id, "chrome", 0), NotAGame, 0b11, ulong.MaxValue, _ => true));
        Assert.Equal(0, cores.Apply(One(child.Id, "chrome", 0), NotAGame, 0b11, ulong.MaxValue, _ => true));
        Assert.Equal(1, opened); // protegido: não é reaberto a cada 10 s
    }

    [Fact]
    public void Lista_vazia_nao_aplica_nada()
    {
        var eco = new BackgroundEcoQos();
        var cores = new BackgroundCores(new AppSettings());
        Assert.Equal(0, eco.Apply(new List<ProcessSnapshot>(), NotAGame, null));
        Assert.Equal(0, cores.Apply(new List<ProcessSnapshot>(), NotAGame, 0b11, ulong.MaxValue, null));
    }
}
