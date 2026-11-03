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
    public void Failed_lookup_does_not_leak_the_token()
    {
        using var self = Process.GetCurrentProcess();
        void Fail() => Assert.ThrowsAny<Exception>(() => NativePerfApi.EnablePrivilege("SeEsteNaoExiste"));
        Fail();
        self.Refresh();
        int before = self.HandleCount;
        const int rounds = 3000;
        for (int i = 0; i < rounds; i++) Fail();
        self.Refresh();
        // Outros testes rodam em paralelo e abrem centenas de handles: a folga é grande, mas um vazamento somaria um por rodada.
        Assert.True(self.HandleCount - before < rounds / 2, $"Handles: {before} → {self.HandleCount}.");
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
        long before = GC.GetAllocatedBytesForCurrentThread();
        CpuInfo.Caches();
        long first = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(first < 8 * 1024, $"Alocou {first} bytes numa leitura.");
    }
}
