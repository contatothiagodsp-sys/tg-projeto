using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization.Tests;

public class SensorRowTests
{
    private static SensorRow Row(SensorType type, float? value, float? max = null, HardwareType hardware = HardwareType.Motherboard, string name = "Sensor")
    {
        var row = new SensorRow { Type = type, Name = name, HardwareType = hardware };
        row.Update(value, null, max);
        return row;
    }

    [Theory]
    [InlineData(0.5f, "50 %")]
    [InlineData(0.75f, "75 %")]
    [InlineData(1f, "100 %")]
    public void Level_as_a_ratio_is_shown_in_percent(float value, string expected)
    {
        Assert.Equal(expected, Row(SensorType.Level, value).Value);
    }

    [Fact]
    public void Level_above_the_ratio_range_is_already_percent_and_stays_so()
    {
        var row = Row(SensorType.Level, 80f);
        Assert.Equal("80 %", row.Value);
        row.Update(0.5f, null, null);
        Assert.Equal("0,5 %", row.Value.Replace('.', ','));
    }

    [Fact]
    public void Level_whose_max_passed_the_ratio_range_is_already_percent()
    {
        Assert.Equal("1 %", Row(SensorType.Level, 1f, max: 80f).Value);
    }

    /// <summary>Valores lidos de um SSD real: multiplicar faria 1 % de desgaste aparecer como 100 %.</summary>
    [Theory]
    [InlineData(HardwareType.Storage, 1f, "1 %")]
    [InlineData(HardwareType.Storage, 91f, "91 %")]
    [InlineData(HardwareType.Battery, 0.5f, "0,5 %")]
    public void Level_of_disks_and_batteries_comes_in_percent_from_the_library(HardwareType hardware, float value, string expected)
    {
        Assert.Equal(expected, Row(SensorType.Level, value, max: value, hardware: hardware).Value.Replace('.', ','));
    }

    [Theory]
    [InlineData("Upload Speed", 1_000_000f, "8 Mbit/s")]
    [InlineData("Download Speed", 125_000_000f, "1000 Mbit/s")]
    public void Network_throughput_is_in_megabits(string name, float bytesPerSecond, string expected)
    {
        Assert.Equal(expected, Row(SensorType.Throughput, bytesPerSecond, hardware: HardwareType.Network, name: name).Value);
    }

    [Theory]
    [InlineData(500f, "500 B/s")]
    [InlineData(2048f, "2 KB/s")]
    [InlineData(3145728f, "3 MB/s")]
    public void Disk_throughput_uses_the_unit_that_fits(float bytesPerSecond, string expected)
    {
        Assert.Equal(expected, Row(SensorType.Throughput, bytesPerSecond, hardware: HardwareType.Storage, name: "Read Rate").Value);
    }

    [Fact]
    public void Data_and_small_data_are_separate_categories()
    {
        Assert.Equal("Dados (GB)", SensorRow.CategoryName(SensorType.Data));
        Assert.Equal("Dados (MB)", SensorRow.CategoryName(SensorType.SmallData));
    }

    [Fact]
    public void Every_sensor_type_has_a_portuguese_category()
    {
        // "Clock" é o termo usado em português também.
        foreach (SensorType type in Enum.GetValues<SensorType>().Where(t => t != SensorType.Clock))
            Assert.NotEqual(type.ToString(), SensorRow.CategoryName(type));
    }

    private static readonly string[] PagesWithSearch = { "PageSensors", "PageGames", "PageBench", "PageDisks" };

    /// <summary>Num Grid sem colunas, o texto de status passa por baixo da caixa de busca quando fica longo.</summary>
    [Fact]
    public void Page_headers_with_a_search_box_keep_it_in_its_own_column()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var doc = XDocument.Load(Path.Combine(RepoText.Root, "MainWindow.xaml"));
        foreach (string page in PagesWithSearch)
        {
            var root = doc.Descendants().Single(e => (string?)e.Attribute(x + "Name") == page);
            foreach (var box in root.Descendants(wpf + "TextBox").Where(t => ((string?)t.Attribute("Tag"))?.StartsWith("Buscar", StringComparison.Ordinal) == true))
            {
                var parent = box.Parent!;
                if (parent.Name != wpf + "Grid" || parent.Elements().Count(e => e.Name != wpf + "Grid.ColumnDefinitions" && e.Name != wpf + "Grid.RowDefinitions") < 2) continue;
                Assert.True(parent.Element(wpf + "Grid.ColumnDefinitions") != null, $"{page}: a busca divide um Grid sem colunas com outro conteúdo.");
                Assert.NotNull(box.Attribute("Grid.Column"));
            }
        }
    }
}
