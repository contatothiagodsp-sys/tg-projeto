using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class CpuSetPlannerTests
{
    /// <summary>CCDs de núcleos com SMT: threads consecutivos do mesmo núcleo, como o Windows numera nos Ryzen.</summary>
    private static CpuTopologySnapshot Ccds(int coresPerCcd, params ulong[] l3PerCcd)
    {
        var cpus = new List<LogicalCpu>();
        var caches = new List<CacheDomain>();
        int index = 0;
        for (int ccd = 0; ccd < l3PerCcd.Length; ccd++)
        {
            int cacheId = index;
            caches.Add(new CacheDomain(cacheId, l3PerCcd[ccd]));
            for (int core = 0; core < coresPerCcd; core++)
            {
                int coreId = index;
                for (int t = 0; t < 2; t++, index++)
                    cpus.Add(new LogicalCpu((uint)(256 + index), 0, (byte)index, coreId, cacheId, 0));
            }
        }
        return new CpuTopologySnapshot(cpus, caches);
    }

    private static CpuTopologySnapshot Hybrid(int pCores, int eCores)
    {
        var cpus = new List<LogicalCpu>();
        int index = 0;
        for (int c = 0; c < pCores; c++)
        {
            int coreId = index;
            for (int t = 0; t < 2; t++, index++) cpus.Add(new LogicalCpu((uint)(256 + index), 0, (byte)index, coreId, 0, 1));
        }
        for (int c = 0; c < eCores; c++, index++) cpus.Add(new LogicalCpu((uint)(256 + index), 0, (byte)index, index, 0, 0));
        return new CpuTopologySnapshot(cpus, new[] { new CacheDomain(0, 36UL << 20) });
    }

    private const ulong Mb = 1UL << 20;
    private static readonly CpuPlanOptions AllOn = new(PreferFastCores: true, UserAvoidCpu0: false, IsolateBackground: true);

    private static void AssertNoOverlap(CpuSetPlan p)
    {
        Assert.Empty(p.GamePreferredCpuSets.Intersect(p.BackgroundCpuSets));
        Assert.Empty(p.GamePreferredCpuSets.Intersect(p.ReservedCpuSets));
        if (p.RestrictsGame) Assert.Equal(0UL, p.GameMask & p.BackgroundMask);
    }

    [Fact]
    public void Single_ccd_like_9800x3d_never_restricts_the_game()
    {
        var p = CpuSetPlanner.Plan(Ccds(8, 96 * Mb), AllOn);
        Assert.Equal(CpuPlanKind.SingleCcd, p.Kind);
        Assert.False(p.RestrictsGame);
        Assert.Empty(p.ReservedCpuSets);
        Assert.Equal(0b1100_0000_0000_0000UL, p.BackgroundMask); // só o último núcleo físico, com os dois threads
    }

    [Fact]
    public void Single_ccd_with_few_cores_leaves_the_background_alone()
    {
        var p = CpuSetPlanner.Plan(Ccds(4, 16 * Mb), AllOn);
        Assert.Empty(p.BackgroundCpuSets);
        Assert.False(p.RestrictsGame);
    }

    [Fact]
    public void Dual_ccd_x3d_prefers_the_big_cache_and_separates_the_background()
    {
        var p = CpuSetPlanner.Plan(Ccds(8, 96 * Mb, 32 * Mb), AllOn);
        Assert.Equal(CpuPlanKind.AsymmetricCache, p.Kind);
        Assert.Equal(0xFFFFUL, p.GameMask);
        Assert.Equal(0xFFFF0000UL, p.BackgroundMask);
        AssertNoOverlap(p);
    }

    [Fact]
    public void Big_cache_is_found_by_size_and_not_by_position()
    {
        var p = CpuSetPlanner.Plan(Ccds(8, 32 * Mb, 96 * Mb), AllOn);
        Assert.Equal(0xFFFF0000UL, p.GameMask);
        Assert.Equal(0xFFFFUL, p.BackgroundMask);
    }

    [Fact]
    public void Dual_ccd_x3d_without_the_option_still_prefers_the_vcache_ccd()
    {
        var t = Ccds(8, 96 * Mb, 32 * Mb); // 7950X3D
        var p = CpuSetPlanner.Plan(t, AllOn with { PreferFastCores = false });
        Assert.True(p.RestrictsGame);
        int big = t.Caches.OrderByDescending(c => c.SizeBytes).First().Id;
        Assert.All(p.GamePreferredCpuSets, c => Assert.Equal(big, c.CacheId));
        Assert.Equal(t.Cpus.Count(c => c.CacheId == big), p.GamePreferredCpuSets.Count);
    }

    [Fact]
    public void Hybrid_without_the_option_leaves_the_game_free()
    {
        var cpus = Enumerable.Range(0, 12).Select(i => new LogicalCpu((uint)i, 0, (byte)i, i, 0, (byte)(i < 8 ? 1 : 0))).ToList();
        var p = CpuSetPlanner.Plan(new CpuTopologySnapshot(cpus, new[] { new CacheDomain(0, 30 * Mb) }), AllOn with { PreferFastCores = false });
        Assert.False(p.RestrictsGame);
    }

    [Fact]
    public void Symmetric_dual_ccd_has_no_evidence_to_pick_one()
    {
        var p = CpuSetPlanner.Plan(Ccds(8, 32 * Mb, 32 * Mb), AllOn);
        Assert.Equal(CpuPlanKind.SymmetricCcds, p.Kind);
        Assert.False(p.RestrictsGame);
        Assert.Equal(2, p.BackgroundCpuSets.Count);
    }

    [Fact]
    public void Hybrid_separates_performance_and_efficient_cores()
    {
        var p = CpuSetPlanner.Plan(Hybrid(8, 16), AllOn);
        Assert.Equal(CpuPlanKind.Hybrid, p.Kind);
        Assert.Equal(16, p.GamePreferredCpuSets.Count);
        Assert.Equal(16, p.BackgroundCpuSets.Count);
        AssertNoOverlap(p);
    }

    [Fact]
    public void User_choice_removes_core_0_with_its_smt_sibling()
    {
        var p = CpuSetPlanner.Plan(Ccds(8, 96 * Mb), AllOn with { UserAvoidCpu0 = true });
        Assert.True(p.RestrictsGame);
        Assert.Equal(2, p.ReservedCpuSets.Count);
        Assert.Equal(0UL, p.GameMask & 0b11);
        AssertNoOverlap(p);
    }

    [Fact]
    public void Too_few_threads_drop_every_restriction()
    {
        var p = CpuSetPlanner.Plan(Ccds(2, 16 * Mb), AllOn with { UserAvoidCpu0 = true });
        Assert.Equal(CpuPlanKind.WindowsDefault, p.Kind);
        Assert.False(p.RestrictsGame);
        Assert.Empty(p.BackgroundCpuSets);
    }

    [Fact]
    public void Avoiding_core_0_is_dropped_when_it_would_leave_too_little()
    {
        var p = CpuSetPlanner.Plan(Ccds(3, 16 * Mb), AllOn with { UserAvoidCpu0 = true });
        Assert.True(p.GamePreferredCpuSets.Count is 0 or >= CpuSetPlanner.MinGameThreads);
        AssertNoOverlap(p);
    }

    [Fact]
    public void Several_groups_stay_with_the_windows_scheduler()
    {
        var cpus = Enumerable.Range(0, 12).Select(i => new LogicalCpu((uint)(256 + i), (ushort)(i / 6), (byte)(i % 6), i % 6, 0, 0)).ToList();
        Assert.Equal(CpuPlanKind.WindowsDefault, CpuSetPlanner.Plan(new CpuTopologySnapshot(cpus, new[] { new CacheDomain(0, Mb) }), AllOn).Kind);
    }

    [Fact]
    public void No_topology_reads_as_windows_default()
    {
        Assert.Equal(CpuPlanKind.WindowsDefault, CpuSetPlanner.Plan(new CpuTopologySnapshot(Array.Empty<LogicalCpu>(), Array.Empty<CacheDomain>()), AllOn).Kind);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(6, 1)]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    [InlineData(12, 2)]
    public void Game_and_background_never_share_a_physical_core(int cores, int ccds)
    {
        var sizes = Enumerable.Range(0, ccds).Select(i => (i == 0 ? 96UL : 32UL) * Mb).ToArray();
        foreach (bool fast in new[] { false, true })
            foreach (bool avoid in new[] { false, true })
                AssertNoOverlap(CpuSetPlanner.Plan(Ccds(cores, sizes), new CpuPlanOptions(fast, avoid, true)));
    }
}

public class CpuTopologyParserTests
{
    private static byte[] CpuSet(uint id, ushort group, byte lp, byte core, byte llc, byte eff)
    {
        var b = new byte[32];
        BitConverter.TryWriteBytes(b.AsSpan(0), 32);
        BitConverter.TryWriteBytes(b.AsSpan(4), 0);
        BitConverter.TryWriteBytes(b.AsSpan(8), id);
        BitConverter.TryWriteBytes(b.AsSpan(12), group);
        b[14] = lp; b[15] = core; b[16] = llc; b[18] = eff;
        return b;
    }

    private static byte[] L3(uint size, ulong mask, ushort group = 0, byte level = 3)
    {
        var b = new byte[64];
        BitConverter.TryWriteBytes(b.AsSpan(0), 2);
        BitConverter.TryWriteBytes(b.AsSpan(4), 64);
        b[8] = level;
        BitConverter.TryWriteBytes(b.AsSpan(12), size);
        BitConverter.TryWriteBytes(b.AsSpan(40), mask);
        BitConverter.TryWriteBytes(b.AsSpan(48), group);
        return b;
    }

    [Fact]
    public void Cpu_sets_are_read_at_the_documented_offsets()
    {
        var cpus = CpuTopologyReader.ParseCpuSets(CpuSet(300, 1, 5, 4, 2, 1));
        Assert.Equal(new LogicalCpu(300, 1, 5, 4, 2, 1), Assert.Single(cpus));
    }

    [Fact]
    public void Truncated_buffer_stops_without_throwing()
    {
        var buf = CpuSet(300, 0, 0, 0, 0, 0);
        Assert.Empty(CpuTopologyReader.ParseCpuSets(buf.AsSpan(0, 20)));
        Assert.Empty(CpuTopologyReader.ParseCpuSets(new byte[16]));
    }

    [Fact]
    public void Only_level_3_counts_as_a_ccd_cache()
    {
        var buf = L3(1 << 20, 0xFF, level: 2).Concat(L3(96u << 20, 0xFFFF)).ToArray();
        var (group, mask, size) = Assert.Single(CpuTopologyReader.ParseL3(buf));
        Assert.Equal((ushort)0, group);
        Assert.Equal(0xFFFFUL, mask);
        Assert.Equal(96UL << 20, size);
    }

    [Fact]
    public void Build_gives_each_ccd_the_size_of_its_cache()
    {
        var cpus = new List<LogicalCpu>
        {
            new(256, 0, 0, 0, 0, 0), new(257, 0, 1, 0, 0, 0),
            new(258, 0, 2, 2, 2, 0), new(259, 0, 3, 2, 2, 0),
        };
        var t = CpuTopologyReader.Build(cpus, new List<(ushort, ulong, ulong)> { (0, 0b0011, 96UL << 20), (0, 0b1100, 32UL << 20) });
        Assert.Equal(2, t.CcdCount);
        Assert.True(t.HasAsymmetricCache);
        Assert.True(t.HasSmt);
        Assert.Equal(2, t.PhysicalCount);
    }

    [Fact]
    public void This_machine_has_a_coherent_topology()
    {
        var t = CpuTopologySnapshot.Current;
        Assert.Equal(Environment.ProcessorCount, t.LogicalCount);
        Assert.True(t.PhysicalCount >= 1 && t.PhysicalCount <= t.LogicalCount);
        Assert.Equal(t.CcdCount, t.Caches.Count);
    }
}

public class TimerLeaseTests
{
    private sealed class FakeApi : ITimerResolutionApi
    {
        public int QueryStatus, SetStatus, Requests, Releases, Queries;
        public uint Coarsest = 156250, Finest = 5000, LastDesired;
        public bool Throws;
        public int Query(out uint coarsest, out uint finest, out uint current)
        {
            Queries++;
            coarsest = Coarsest; finest = Finest; current = 156250;
            return QueryStatus;
        }
        public int Set(uint desired, bool set, out uint current)
        {
            if (Throws) throw new EntryPointNotFoundException();
            if (set) { Requests++; LastDesired = desired; } else Releases++;
            current = set ? desired : 156250;
            return SetStatus;
        }
    }

    [Fact]
    public void Acquire_twice_makes_a_single_request()
    {
        var api = new FakeApi();
        using var lease = new WindowsTimerResolutionLease(api);
        Assert.Equal(5000u, lease.Acquire(5000));
        Assert.Equal(5000u, lease.Acquire(5000));
        Assert.Equal(1, api.Requests);
        Assert.Equal(1, api.Queries);
    }

    [Fact]
    public void Release_twice_gives_back_only_once()
    {
        var api = new FakeApi();
        using var lease = new WindowsTimerResolutionLease(api);
        lease.Acquire(5000);
        lease.Release();
        lease.Release();
        Assert.Equal(1, api.Releases);
        Assert.False(lease.Held);
        Assert.Null(lease.EffectiveResolution);
    }

    [Fact]
    public void Dispose_gives_back_what_is_held()
    {
        var api = new FakeApi();
        var lease = new WindowsTimerResolutionLease(api);
        lease.Acquire(5000);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, api.Releases);
        Assert.Null(lease.Acquire(5000));
        Assert.Equal(1, api.Requests);
    }

    [Fact]
    public void Release_without_acquire_touches_nothing()
    {
        var api = new FakeApi();
        new WindowsTimerResolutionLease(api).Release();
        Assert.Equal(0, api.Releases);
    }

    [Fact]
    public void A_machine_that_only_supports_1ms_gets_1ms()
    {
        var api = new FakeApi { Finest = 10000 };
        using var lease = new WindowsTimerResolutionLease(api);
        Assert.Equal(10000u, lease.Acquire(5000));
        Assert.Equal(10000u, api.LastDesired);
    }

    [Fact]
    public void Failed_query_requests_nothing()
    {
        var api = new FakeApi { QueryStatus = unchecked((int)0xC0000022) };
        using var lease = new WindowsTimerResolutionLease(api);
        Assert.Null(lease.Acquire(5000));
        Assert.Equal(0, api.Requests);
    }

    [Fact]
    public void Refused_request_leaves_nothing_to_release()
    {
        var api = new FakeApi { SetStatus = unchecked((int)0xC0000022) };
        var lease = new WindowsTimerResolutionLease(api);
        Assert.Null(lease.Acquire(5000));
        Assert.False(lease.Held);
        lease.Release();
        Assert.Equal(0, api.Releases);
    }

    [Fact]
    public void Failed_release_is_not_repeated()
    {
        var api = new FakeApi();
        var lease = new WindowsTimerResolutionLease(api);
        lease.Acquire(5000);
        api.SetStatus = unchecked((int)0xC0000022);
        lease.Release();
        lease.Release();
        Assert.Equal(1, api.Releases);
        Assert.False(lease.Held);
    }

    [Fact]
    public void Cancelled_token_requests_nothing()
    {
        var api = new FakeApi();
        using var lease = new WindowsTimerResolutionLease(api);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(lease.Acquire(5000, cts.Token));
        Assert.Equal(0, api.Requests);
    }

    [Fact]
    public void Missing_entry_point_does_not_throw()
    {
        var api = new FakeApi { Throws = true };
        using var lease = new WindowsTimerResolutionLease(api);
        Assert.Null(lease.Acquire(5000));
        Assert.False(lease.Held);
    }

    [Theory]
    [InlineData(5000u, 156250u, 5000u, 5000u)]
    [InlineData(1000u, 156250u, 5000u, 5000u)]   // mais fino que o Windows aceita
    [InlineData(200000u, 156250u, 5000u, 156250u)] // mais lento que o limite
    public void Clamp_respects_the_limits(uint target, uint coarsest, uint finest, uint expected)
    {
        Assert.Equal(expected, WindowsTimerResolutionLease.Clamp(target, coarsest, finest));
    }
}
