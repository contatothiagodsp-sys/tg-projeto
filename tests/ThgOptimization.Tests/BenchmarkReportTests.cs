using System.IO;
using ThgOptimization.Core.Benchmark;
using ThgOptimization.Views;

namespace ThgOptimization.Tests;

[Collection(WpfCollection.Name)]
public class BenchmarkReportTests
{
    private static AbComparison Result(string labelA = "Padrão", string labelB = "XMP") =>
        FrametimeChartTests.Sample() with
        {
            ComparedAt = new DateTime(2026, 11, 3, 20, 15, 30),
            A = FrametimeChartTests.Sample().A with { ConfigLabel = labelA },
            B = FrametimeChartTests.Sample().B with { ConfigLabel = labelB },
        };

    [Fact]
    public void Html_has_every_metric_the_verdict_and_the_colors()
    {
        string html = BenchmarkReport.BuildHtml(Result(), null);
        string shown = System.Net.WebUtility.HtmlDecode(html); // o que o navegador mostra
        foreach (string text in new[] { "FPS médio", "1% low", "0,1% low", "Engasgos", "P99", "P99,9", "t de Student", "d de Cohen", "Melhora significativa (p < 0.05" })
            Assert.Contains(text, shown);
        Assert.Contains("class=\"good\">+", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Chart_is_embedded_so_the_file_works_alone()
    {
        string html = BenchmarkReport.BuildHtml(Result(), new byte[] { 1, 2, 3 });
        Assert.Contains("src=\"data:image/png;base64,AQID\"", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Labels_cannot_inject_html()
    {
        string html = BenchmarkReport.BuildHtml(Result("<script>alert(1)</script>", "b&b"), null);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("b&amp;b", html);
    }

    [Fact]
    public void Labels_cannot_leave_the_report_folder()
    {
        string name = BenchmarkReport.FileName(Result(@"..\..\Windows", "a/b:c?"));
        Assert.Equal(name, Path.GetFileName(name));
        Assert.StartsWith("20261103-201530-", name);
        Assert.EndsWith(".html", name);
    }

    [Fact]
    public void Report_is_saved_in_the_given_folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "thg-lab-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = BenchmarkReport.Save(Result(), null, folder);
            Assert.Equal(folder, Path.GetDirectoryName(path));
            Assert.Contains("Performance Lab", File.ReadAllText(path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Default_folder_is_in_documents()
    {
        Assert.EndsWith(Path.Combine("tHgOptimization", "BenchLab"), BenchmarkReport.Folder);
    }

    [Fact]
    public void Chart_renders_off_screen_to_a_png()
    {
        Sta.Run(() =>
        {
            FrametimeChartTests.EnsureTheme();
            byte[] png = FrametimeCompareChart.RenderPng(Result(), FrametimeChartTests.Curve(7, 1), FrametimeChartTests.Curve(6, 2));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4));
            Assert.True(png.Length > 5000);
        });
    }
}
