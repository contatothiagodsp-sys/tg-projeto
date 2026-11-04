using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibreHardwareMonitor.Hardware;
using ThgOptimization.UI.ViewModels;

namespace ThgOptimization.Tests;

/// <summary>
/// Monta a página de Sensores de verdade, a partir do XAML e com o modelo real, sem abrir a janela. Com THG_RENDER_DIR definida, grava um PNG
/// para conferir o layout a olho; com THG_HARDWARE_TESTS=1 também, usa os sensores deste PC.
/// </summary>
[Collection(WpfCollection.Name)]
public class SensorPageRenderTests
{
    private static SensorRow Row(string id, string hardware, HardwareType kind, SensorType type, string name, float? value, float? max = null)
    {
        var row = new SensorRow { Id = id, Hardware = hardware, HardwareType = kind, Type = type, Category = SensorRow.CategoryName(type), Name = name };
        row.Update(value, value, max ?? value);
        return row;
    }

    private static SensorMonitor FakeMonitor()
    {
        var m = new SensorMonitor();
        m.Rows.Add(Row("c/t", "AMD Ryzen 7 9800X3D", HardwareType.Cpu, SensorType.Temperature, "Core (Tctl/Tdie)", 53));
        m.Rows.Add(Row("c/l", "AMD Ryzen 7 9800X3D", HardwareType.Cpu, SensorType.Load, "CPU Total", 12));
        m.Rows.Add(Row("c/k", "AMD Ryzen 7 9800X3D", HardwareType.Cpu, SensorType.Clock, "Core #1", 4700));
        for (int i = 2; i <= 8; i++) m.Rows.Add(Row($"c/k{i}", "AMD Ryzen 7 9800X3D", HardwareType.Cpu, SensorType.Clock, $"Core #{i}", 4600 + i));
        m.Rows.Add(Row("g/t", "AMD Radeon RX 9070 XT", HardwareType.GpuAmd, SensorType.Temperature, "GPU Core", 85));
        m.Rows.Add(Row("g/p", "AMD Radeon RX 9070 XT", HardwareType.GpuAmd, SensorType.Power, "GPU Package", 220));
        m.Rows.Add(Row("d/l", "KINGSTON SNV2S1000G", HardwareType.Storage, SensorType.Level, "Percentage Used", 2));
        m.Rows.Add(Row("d/r", "KINGSTON SNV2S1000G", HardwareType.Storage, SensorType.Throughput, "Read Rate", 58056));
        m.Rows.Add(Row("n/u", "Ethernet", HardwareType.Network, SensorType.Throughput, "Upload Speed", 4044));
        return m;
    }

    private static IEnumerable<T> Find<T>(DependencyObject o) where T : DependencyObject
    {
        if (o is T hit) yield return hit;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(o, i))) yield return child;
    }

    [Theory]
    [InlineData(860, 560, "minima")]
    [InlineData(1060, 760, "normal")]
    public void Page_lays_out_with_data_and_nothing_overlaps(int width, int height, string name)
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/tHgOptimization;component/") };
            var win = (Window)XamlReader.Parse(XamlSmokeTests.Loose(File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml"))), context);
            var page = (Grid)win.FindName("PageSensors");
            ((Panel)page.Parent).Children.Remove(page);
            page.Margin = new Thickness(36, 32, 36, 32);
            page.Visibility = Visibility.Visible;

            var settings = new AppSettings();
            using var monitor = Environment.GetEnvironmentVariable("THG_HARDWARE_TESTS") == "1" ? RealMonitor() : FakeMonitor();
            var vm = new SensorPageViewModel();
            vm.SetMonitorState(open: true);
            vm.Rebuild(monitor, settings);
            foreach (string id in monitor.Rows.Where(r => r.Type is SensorType.Temperature or SensorType.Load).Take(4).Select(r => r.Id).ToList()) vm.TogglePin(id, settings);
            for (int i = 0; i < 20; i++) { foreach (var r in monitor.Rows) r.Update(r.RawValue + (i % 4), r.RawMin, r.RawMax); vm.Refresh(monitor); }

            ((ItemsControl)win.FindName("SensorPinnedCards")).ItemsSource = vm.Pinned;
            ((FrameworkElement)win.FindName("SensorPinnedCards")).Visibility = Visibility.Visible;
            ((FrameworkElement)win.FindName("SensorPinnedEmpty")).Visibility = Visibility.Collapsed;
            ((ItemsControl)win.FindName("SensorGroups")).ItemsSource = vm.Groups;
            ((TextBlock)win.FindName("SensorStatusLine")).Text = vm.StatusLine;
            var chipStyle = (Style)win.FindResource("Chip");
            void Chips(string panel, IEnumerable<string> labels)
            {
                bool first = true;
                foreach (string label in labels)
                {
                    ((Panel)win.FindName(panel)).Children.Add(new RadioButton { Content = label, GroupName = panel, Style = chipStyle, IsChecked = first });
                    first = false;
                }
            }
            Chips("SensorHardwareChips", new[] { "Todos" }.Concat(vm.HardwareFilters));
            Chips("SensorCategoryChips", new[] { "Todos" }.Concat(vm.CategoryFilters));
            Chips("SensorShowChips", new[] { "Todos", "Favoritos", "Indisponíveis" });

            var host = new Border { Background = (Brush)Application.Current.FindResource("Bg"), Child = page };
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();

            // A área de sensores não pode sumir, mesmo na janela no tamanho mínimo.
            var list = (FrameworkElement)win.FindName("SensorScroll");
            Assert.True(list.ActualHeight >= 120, $"A lista ficou com {list.ActualHeight} px em {width}×{height}.");

            // Status e busca em colunas separadas: nunca um por cima do outro.
            var status = (FrameworkElement)win.FindName("SensorStatusLine");
            var search = (FrameworkElement)win.FindName("SensorSearch");
            double statusRight = status.TranslatePoint(new Point(status.ActualWidth, 0), page).X;
            double searchLeft = search.TranslatePoint(new Point(0, 0), page).X;
            Assert.True(statusRight <= searchLeft, $"O status termina em {statusRight} e a busca começa em {searchLeft}.");

            // As linhas foram geradas e mostram o valor formatado.
            Assert.Contains(Find<TextBlock>(page), t => t.Text.Contains('°'));

            if (Environment.GetEnvironmentVariable("THG_RENDER_DIR") is { Length: > 0 } dir)
            {
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(dir, $"sensores-{name}.png"));
                png.Save(file);
            }
        });
    }

    private static SensorMonitor RealMonitor()
    {
        var m = new SensorMonitor();
        m.OpenAsync().GetAwaiter().GetResult();
        m.RefreshAsync(light: false).GetAwaiter().GetResult();
        return m;
    }
}
