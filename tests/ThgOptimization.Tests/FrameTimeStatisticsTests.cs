using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Tests;

public class FrameTimeStatisticsTests
{
    private static BenchmarkSession S(double fps, string slot = "A") =>
        new("jogo", slot, new DateTime(2026, 11, 3), fps, fps * 0.7, fps * 0.5, 1000 / (fps * 0.7), 1, 1000 / fps, 6000, 60, null) { Slot = slot };

    private static BenchmarkSession[] Many(string slot, params double[] fps) => fps.Select(f => S(f, slot)).ToArray();

    [Fact]
    public void Real_improvement_is_classified_as_significant()
    {
        var r = FrameTimeStatistics.Compare(Many("A", 100, 101, 99, 100, 102), Many("B", 110, 111, 109, 112, 110), aIsBaseline: true);
        Assert.Equal(AbTestMethod.MannWhitney, r.Method);
        Assert.True(r.IsStatisticallySignificant);
        Assert.Equal(AbVerdictKind.Improvement, r.Kind);
        Assert.StartsWith("Melhora significativa (p < 0.05, d = ", r.Verdict);
        Assert.Equal(9.9, r.AvgFpsDeltaPct, 0);
    }

    [Fact]
    public void Noise_is_not_called_an_improvement()
    {
        var r = FrameTimeStatistics.Compare(Many("A", 100, 108, 93, 104, 97), Many("B", 102, 95, 109, 99, 105), aIsBaseline: true);
        Assert.False(r.IsStatisticallySignificant);
        Assert.Equal(AbVerdictKind.Noise, r.Kind);
        Assert.Equal("Melhora dentro do ruído", r.Verdict);
    }

    [Fact]
    public void Real_regression_is_called_worse()
    {
        var r = FrameTimeStatistics.Compare(Many("A", 110, 111, 109, 112, 110), Many("B", 100, 101, 99, 100, 102), aIsBaseline: true);
        Assert.True(r.IsStatisticallySignificant);
        Assert.Equal(AbVerdictKind.Regression, r.Kind);
        Assert.StartsWith("Piora significativa", r.Verdict);
        Assert.True(r.EffectSizeCohensD < 0);
    }

    [Fact]
    public void Negligible_effect_is_trivial()
    {
        var r = FrameTimeStatistics.Compare(Many("A", 90, 100, 110), Many("B", 91, 101, 110), aIsBaseline: true);
        Assert.Equal(AbVerdictKind.Trivial, r.Kind);
        Assert.Equal("Diferença trivial", r.Verdict);
    }

    [Fact]
    public void One_capture_each_is_inconclusive_whatever_the_difference()
    {
        var r = FrameTimeStatistics.Compare(S(100), S(200, "B"), aIsBaseline: true);
        Assert.Equal(AbTestMethod.None, r.Method);
        Assert.Null(r.PValue);
        Assert.False(r.IsStatisticallySignificant);
        Assert.Equal(AbVerdictKind.Inconclusive, r.Kind);
        Assert.Equal(100, r.AvgFpsDeltaPct, 6); // o delta continua sendo mostrado
    }

    [Theory] // o menor p exato do Mann-Whitney precisa alcançar 0,05: 2/C(6,3) = 0,10 e 2/C(7,3) = 0,057 não alcançam
    [InlineData(1, 1, AbTestMethod.None)]
    [InlineData(1, 2, AbTestMethod.StudentT)]
    [InlineData(2, 2, AbTestMethod.StudentT)]
    [InlineData(3, 3, AbTestMethod.StudentT)]
    [InlineData(3, 4, AbTestMethod.StudentT)]
    [InlineData(3, 5, AbTestMethod.MannWhitney)]
    [InlineData(4, 4, AbTestMethod.MannWhitney)]
    public void Method_follows_what_each_test_can_reach(int n1, int n2, AbTestMethod expected)
    {
        Assert.Equal(expected, FrameTimeStatistics.ChooseMethod(n1, n2));
    }

    [Fact]
    public void Three_captures_each_can_reach_significance()
    {
        var r = FrameTimeStatistics.Compare(Many("A", 100, 101, 99), Many("B", 110, 111, 109), aIsBaseline: true);
        Assert.Equal(AbTestMethod.StudentT, r.Method);
        Assert.True(r.IsStatisticallySignificant);
    }

    [Fact]
    public void Baseline_flag_flips_the_direction()
    {
        var a = Many("A", 110, 111, 109);
        var b = Many("B", 100, 101, 99);
        var r = FrameTimeStatistics.Compare(a, b, aIsBaseline: false);
        Assert.Equal(AbVerdictKind.Improvement, r.Kind);
        Assert.Equal(10, r.AvgFpsDeltaPct, 6);
        Assert.Equal(110, r.A.AvgFps);
        Assert.Equal(100, r.B.AvgFps);
    }

    [Fact]
    public void Summary_uses_medians_so_one_bad_capture_does_not_move_it()
    {
        var r = FrameTimeStatistics.Compare(Many("A", 100, 100, 10), Many("B", 100, 100, 100), aIsBaseline: true);
        Assert.Equal(100, r.A.AvgFps);
        Assert.Equal(0, r.AvgFpsDeltaPct, 6);
    }

    [Fact]
    public void Graph_uses_the_capture_closest_to_the_median()
    {
        var a = new[] { S(100) with { CsvPath = "longe" }, S(150) with { CsvPath = "mediana" }, S(151) with { CsvPath = "outra" } };
        Assert.Equal("mediana", FrameTimeStatistics.Compare(a, a, aIsBaseline: true).A.CsvPath);
    }

    [Theory] // valores de tabela da distribuição t
    [InlineData(2.776, 4, 0.05)]
    [InlineData(2.228, 10, 0.05)]
    [InlineData(1.0, 1, 0.5)]
    [InlineData(0.0, 8, 1.0)]
    public void Student_p_matches_published_tables(double t, int df, double p)
    {
        Assert.Equal(p, FrameTimeStatistics.StudentTwoSidedP(t, df), 3);
    }

    [Fact]
    public void Cohens_d_matches_the_formula()
    {
        // médias 2 e 4, variância 1 nos dois lados: d = 2 / 1
        Assert.Equal(2, FrameTimeStatistics.CohensD(new double[] { 1, 2, 3 }, new double[] { 3, 4, 5 }), 10);
    }

    [Fact]
    public void Zero_variance_gives_finite_values_the_settings_file_can_store()
    {
        var r = FrameTimeStatistics.Compare(Many("A", 100, 100, 100), Many("B", 110, 110, 110), aIsBaseline: true);
        Assert.True(double.IsFinite(r.EffectSizeCohensD));
        Assert.True(r.IsStatisticallySignificant);
        var same = FrameTimeStatistics.Compare(Many("A", 100, 100, 100), Many("B", 100, 100, 100), aIsBaseline: true);
        Assert.Equal(0, same.EffectSizeCohensD);
        Assert.Equal(1, same.PValue);
    }

    [Fact]
    public void Mann_whitney_full_separation_gives_the_minimum_exact_p()
    {
        Assert.Equal(2.0 / 70, FrameTimeStatistics.MannWhitneyP(new double[] { 1, 2, 3, 4 }, new double[] { 5, 6, 7, 8 }), 10);
    }

    [Theory]
    [InlineData(new double[] { 1, 2, 2, 3 }, new double[] { 2, 3, 4, 4 })]
    [InlineData(new double[] { 5, 5, 5 }, new double[] { 5, 5, 5 })]
    [InlineData(new double[] { 1, 3, 5, 7, 9 }, new double[] { 2, 4, 6 })]
    public void Mann_whitney_exact_p_with_ties_matches_brute_force(double[] first, double[] second)
    {
        Assert.Equal(BruteForceP(first, second), FrameTimeStatistics.MannWhitneyP(first, second), 10);
    }

    [Fact]
    public void Mann_whitney_large_samples_use_a_normal_approximation_close_to_the_exact_value()
    {
        var first = Enumerable.Range(0, 30).Select(i => (double)i * 2).ToArray();
        var second = Enumerable.Range(0, 30).Select(i => i * 2 + 9.0).ToArray();
        double p = FrameTimeStatistics.MannWhitneyP(first, second);
        // Conta feita à mão: U = 1+2+…+25 = 325, z = (|325-450|-0,5)/raiz(30·30·61/12) = 1,84, p = 0,066.
        Assert.Equal(0.066, p, 3);
    }

    /// <summary>Referência da fórmula: percorre todas as divisões dos valores em dois grupos.</summary>
    private static double BruteForceP(double[] first, double[] second)
    {
        var all = first.Concat(second).ToArray();
        double Sum(IEnumerable<double> group) => group.Sum(v => all.Count(x => x < v) * 2 + all.Count(x => x == v) + 1);
        double observed = Sum(first);
        int total = 0, low = 0, high = 0;
        for (int mask = 0; mask < 1 << all.Length; mask++)
        {
            if (System.Numerics.BitOperations.PopCount((uint)mask) != first.Length) continue;
            double s = Sum(all.Where((_, i) => (mask >> i & 1) == 1));
            total++;
            if (s <= observed) low++;
            if (s >= observed) high++;
        }
        return Math.Min(1, 2.0 * Math.Min(low, high) / total);
    }
}
