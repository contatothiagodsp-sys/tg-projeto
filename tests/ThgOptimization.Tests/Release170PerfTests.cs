using System.Diagnostics;
using ThgOptimization.Core.Oc;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

#pragma warning disable THGOC001 // só o formato do pedido, sem driver
public class PawnIoRequestTests
{
    [Fact]
    public void Request_has_the_name_padded_to_32_bytes_and_then_the_values()
    {
        Span<byte> request = stackalloc byte[PawnIoTransport.RequestSize(2)];
        PawnIoTransport.BuildRequest("ioctl_read", stackalloc ulong[] { 0x1122334455667788, 2 }, request);
        Assert.Equal(48, request.Length);
        Assert.Equal((byte)'i', request[0]);
        Assert.Equal(0, request[10]);
        Assert.Equal(0, request[31]);
        Assert.Equal(0x88, request[32]);
        Assert.Equal(0x11, request[39]);
        Assert.Equal(2, request[40]);
    }

    [Fact]
    public void Building_the_request_does_not_allocate()
    {
        Span<ulong> values = stackalloc ulong[] { 1 };
        Span<byte> request = stackalloc byte[PawnIoTransport.RequestSize(1)];
        PawnIoTransport.BuildRequest("ioctl_read_smu_register", values, request);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) PawnIoTransport.BuildRequest("ioctl_read_smu_register", values, request);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
#pragma warning restore THGOC001

public class TopologyCountsTests
{
    [Fact]
    public void Reading_the_counts_in_a_loop_does_not_allocate()
    {
        var t = new CpuTopologySnapshot(
            new[] { new LogicalCpu(0, 0, 0, 0, 0, 0), new LogicalCpu(1, 0, 1, 0, 0, 0), new LogicalCpu(2, 0, 2, 2, 1, 0), new LogicalCpu(3, 0, 3, 2, 1, 0) },
            new[] { new CacheDomain(0, 96), new CacheDomain(1, 32) });
        long sum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            sum += t.LogicalCount + t.PhysicalCount + t.GroupCount + t.CcdCount + (t.IsHybrid ? 1 : 0) + (t.HasAsymmetricCache ? 1 : 0) + (t.HasSmt ? 1 : 0) + t.Summary.Length;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sum > 0);
        Assert.Equal("2 núcleos físicos, 4 threads, 2 CCD(s), L3 assimétrico", t.Summary);
    }
}

public class CpuSetIdsTests
{
    private static readonly LogicalCpu[] Cpus =
    {
        new(256, 0, 0, 0, 0, 0), new(257, 0, 1, 0, 0, 0), new(258, 0, 2, 2, 0, 0), new(259, 1, 3, 4, 0, 0),
    };

    [Fact]
    public void Mask_selects_the_ids_of_group_zero()
    {
        Assert.Equal(new uint[] { 256, 258 }, CpuTopology.CpuSetIdsFor(Cpus, 0b0101));
    }

    [Fact]
    public void Other_groups_are_never_selected()
    {
        Assert.Empty(CpuTopology.CpuSetIdsFor(Cpus, 0b1000));
    }

    [Fact]
    public void Empty_mask_selects_nothing()
    {
        Assert.Empty(CpuTopology.CpuSetIdsFor(Cpus, 0));
    }

    [Fact]
    public void Real_topology_gives_ids_for_every_core_of_group_zero()
    {
        var current = CpuTopologySnapshot.Current;
        Assert.Equal(current.Cpus.Count(c => c.Group == 0 && c.Index < 64), CpuTopology.CpuSetIdsFor(current.Cpus, ulong.MaxValue).Length);
    }
}

#pragma warning disable THGOC001 // ponteiros nulos: se o guarda falhar, a chamada nem chega a voltar
public class AdlxDisposedTests
{
    [Fact]
    public void Open_after_dispose_fails_without_loading_anything()
    {
        var io = new AmdAdlxOverdriveTransport(0, 0, 0);
        io.Dispose();
        Assert.False(io.TryGetOffsetMhz(out _));
        Assert.Contains("descartado", io.LastError);
        Assert.Null(io.TempC());
        using (OcGate.AllowInTests()) Assert.False(io.TrySetOffsetMhz(100));
    }

    [Fact]
    public void Dispose_twice_is_harmless()
    {
        var io = new AmdAdlxOverdriveTransport(0, 0, 0);
        io.Dispose();
        io.Dispose();
    }
}
#pragma warning restore THGOC001

public class PrivilegeTests
{
    [Fact]
    public void Privilege_not_granted_is_a_clear_error()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() => NativePerfApi.ThrowIfNotAssigned(NativePerfApi.ErrorNotAllAssigned, "SeTeste"));
        Assert.Contains("SeTeste", ex.Message);
        Assert.Contains("administrador", ex.Message);
    }

    [Fact]
    public void Privilege_granted_passes()
    {
        NativePerfApi.ThrowIfNotAssigned(0, "SeTeste");
    }
}

public class TokenHandleTests
{
    [Fact]
    public void Privilege_every_process_already_has_goes_through_the_whole_path()
    {
        NativePerfApi.EnablePrivilege("SeChangeNotifyPrivilege"); // ligado por padrão em todo token: nada muda
    }

    [Fact]
    public void Failed_lookup_does_not_leak_the_token()
    {
        using var self = Process.GetCurrentProcess();
        void Fail() => Assert.ThrowsAny<Exception>(() => NativePerfApi.EnablePrivilege("SeEsteNaoExiste"));
        Fail();
        // Outros testes abrem e fecham centenas de handles em paralelo. Um vazamento somaria em toda rodada: a menor diferença é a que conta.
        const int rounds = 500;
        int smallest = int.MaxValue;
        for (int batch = 0; batch < 6; batch++)
        {
            self.Refresh();
            int before = self.HandleCount;
            for (int i = 0; i < rounds; i++) Fail();
            self.Refresh();
            smallest = Math.Min(smallest, self.HandleCount - before);
        }
        Assert.True(smallest < rounds / 2, $"Menor aumento de handles por lote: {smallest}.");
    }
}

public class CpuCachesTests
{
    [Fact]
    public void Repeated_reads_give_the_same_caches()
    {
        var first = CpuInfo.Caches();
        Assert.NotEmpty(first);
        for (int i = 0; i < 50; i++) Assert.Equal(first, CpuInfo.Caches());
    }

    [Fact]
    public void Read_buffer_comes_from_the_pool()
    {
        CpuInfo.Caches();
        // A menor de várias leituras: o ArrayPool é compartilhado com os testes em paralelo.
        long smallest = long.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            CpuInfo.Caches();
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.True(smallest < 8 * 1024, $"Alocou {smallest} bytes numa leitura.");
    }
}

public class FrameEventCountTests
{
    [Fact]
    public void Count_is_exact_with_several_threads()
    {
        using var m = new FrameMonitor();
        const int threads = 8, events = 50_000;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (int i = 0; i < events; i++) m.OnPresent(0x7000, i);
        });
        Assert.Equal(threads * events, m.EventsReceived);
    }
}

public class PreferredCoresQueryTests
{
    [Theory]
    [InlineData(false, 122, 4u, true)]
    [InlineData(true, 0, 0u, false)]
    [InlineData(false, 5, 4u, false)]
    [InlineData(false, 87, 0u, false)]
    [InlineData(false, 122, 0u, false)]
    public void Answer_depends_on_why_the_call_failed(bool ok, int error, uint needed, bool expected)
    {
        Assert.Equal(expected, CpuTopology.PreferredCoresPresent(ok, error, needed));
    }
}
