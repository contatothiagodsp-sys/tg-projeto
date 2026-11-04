using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Tests;

public class FrameTimeMedianTests
{
    [Fact]
    public void Odd_even_empty_and_single()
    {
        Assert.Equal(2, FrameTimeStatistics.Median(new double[] { 3, 1, 2 }));
        Assert.Equal(2.5, FrameTimeStatistics.Median(new double[] { 4, 1, 3, 2 }));
        Assert.Equal(0, FrameTimeStatistics.Median(Array.Empty<double>()));
        Assert.Equal(7, FrameTimeStatistics.Median(new double[] { 7 }));
    }

    /// <summary>Uma sequência de tamanho desconhecido, maior que o buffer inicial: ele precisa crescer sem perder valores.</summary>
    [Fact]
    public void Sequence_of_unknown_size_grows_the_buffer()
    {
        static IEnumerable<double> Descending(int n) { for (int i = n; i > 0; i--) yield return i; }
        Assert.Equal(500.5, FrameTimeStatistics.Median(Descending(1000)));
        Assert.Equal(501, FrameTimeStatistics.Median(Descending(1001)));
    }

    [Fact]
    public void Same_answer_as_sorting_everything()
    {
        var random = new Random(42);
        for (int round = 0; round < 200; round++)
        {
            var values = Enumerable.Range(0, random.Next(1, 200)).Select(_ => random.NextDouble() * 100).ToArray();
            var sorted = values.OrderBy(x => x).ToArray();
            int n = sorted.Length;
            double expected = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
            Assert.Equal(expected, FrameTimeStatistics.Median(values));
        }
    }

    [Fact]
    public void Original_values_stay_in_place()
    {
        double[] values = { 3, 1, 2 };
        FrameTimeStatistics.Median(values);
        Assert.Equal(new double[] { 3, 1, 2 }, values);
    }

    [Fact]
    public void Large_array_is_not_copied_to_the_heap()
    {
        var values = Enumerable.Range(0, 50_000).Select(i => (double)i).ToArray();
        FrameTimeStatistics.Median(values); // primeiro empréstimo: o pool ainda pode criar o buffer
        long before = GC.GetAllocatedBytesForCurrentThread();
        FrameTimeStatistics.Median(values);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024, $"Alocou {allocated} bytes.");
    }
}
