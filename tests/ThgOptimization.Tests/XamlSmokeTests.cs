using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;

namespace ThgOptimization.Tests;

/// <summary>
/// O compilador não confere StaticResource nem bindings: um erro aí só aparece ao abrir o app. Aqui a janela é montada de
/// verdade a partir do XAML, mas sem o code-behind, então nenhum hardware, tarefa agendada ou registro é tocado.
/// </summary>
[Collection(WpfCollection.Name)]
public class XamlSmokeTests
{
    private static string RepoFile(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tHgOptimization.csproj"))) return Path.Combine(d.FullName, name);
        throw new FileNotFoundException(name);
    }

    internal static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    internal static void EnsureTheme()
    {
        if (Application.Current != null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/tHgOptimization;component/Theme.xaml") });
    }

    /// <summary>Tira o x:Class e os manipuladores de evento, que o leitor de XAML solto não aceita.</summary>
    internal static string Loose(string xaml)
    {
        xaml = Regex.Replace(xaml, @"\sx:Class=""[^""]+""", "");
        xaml = Regex.Replace(xaml, @"\s(?:Checked|Click|LostFocus|MouseLeftButtonUp|MouseLeftButtonDown|PasswordChanged|PreviewKeyDown|KeyDown|SelectionChanged|SizeChanged|TextChanged|Unchecked|ValueChanged|Loaded|Closing|Closed|MouseEnter|MouseLeave|PreviewMouseDown|MouseDown|DragEnter|Drop|PreviewMouseWheel)=""[^""{]+""", "");
        return xaml.Replace(@"xmlns:local=""clr-namespace:ThgOptimization""", @"xmlns:local=""clr-namespace:ThgOptimization;assembly=tHgOptimization""")
                   .Replace(@"xmlns:views=""clr-namespace:ThgOptimization.Views""", @"xmlns:views=""clr-namespace:ThgOptimization.Views;assembly=tHgOptimization""")
                   .Replace(@"xmlns:ui=""clr-namespace:ThgOptimization.UI""", @"xmlns:ui=""clr-namespace:ThgOptimization.UI;assembly=tHgOptimization""");
    }

    [Fact]
    public void Main_window_xaml_loads_with_every_resource_resolved()
    {
        OnSta(() =>
        {
            EnsureTheme();
            // Base no assembly do app: é de lá que saem os caminhos relativos, como o ícone da janela.
            var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/tHgOptimization;component/") };
            var win = (Window)XamlReader.Parse(Loose(File.ReadAllText(RepoFile("MainWindow.xaml"))), context);
            Assert.NotNull(win.FindName("OcSetupCpu"));
            Assert.NotNull(win.FindName("LightDevices"));
            Assert.NotNull(win.FindName("ChkLightAutoOrder"));
            Assert.Null(win.FindName("LightAdvanced"));
            Assert.NotNull(win.FindName("OptTweaksSummary"));
            Assert.NotNull(win.FindName("SetMemStats"));
            // 1.6.5 e 1.6.6: ordem manual só em Avançado, recolhido; perfis do overlay e diagnóstico em Configurações.
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)win.FindName("LightAdvancedPanel")).Visibility);
            Assert.NotNull(win.FindName("LightOrderOverrides"));
            Assert.NotNull(win.FindName("LightFpsCombo"));
            Assert.NotNull(win.FindName("OvProfileEco"));
            Assert.NotNull(win.FindName("OvProfileDet"));
            Assert.NotNull(win.FindName("SetDiagOutput"));
        });
    }

    [Fact]
    public void Bench_page_opens_with_the_lab_above_the_collapsed_benchmarks()
    {
        OnSta(() =>
        {
            EnsureTheme();
            var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/tHgOptimization;component/") };
            var win = (Window)XamlReader.Parse(Loose(File.ReadAllText(RepoFile("MainWindow.xaml"))), context);
            var lab = (FrameworkElement)win.FindName("LabCard");
            var old = (Expander)win.FindName("BenchIndividual");
            var column = (StackPanel)lab.Parent;
            Assert.Same(column, old.Parent);
            Assert.True(column.Children.IndexOf(lab) < column.Children.IndexOf(old));
            Assert.False(old.IsExpanded);
            // Os benchmarks continuam acessíveis, e a lateral de cada um deixa a roda do mouse passar para a página.
            foreach (string panel in new[] { "BenchPanelRender", "BenchPanelGpu", "BenchPanelRt", "BenchPanelSim", "BenchPanelMem", "BenchPanelDisk" })
            {
                var grid = (Grid)win.FindName(panel);
                var inner = grid.Children.OfType<ScrollViewer>().ToList();
                Assert.NotEmpty(inner);
                Assert.All(inner, v => Assert.True(ThgOptimization.UI.ScrollChain.GetForwardAtEdges(v), panel));
            }
            foreach (string button in new[] { "BtnLabCaptureA", "BtnLabCaptureB", "BtnLabRepeat", "BtnLabCompare", "BtnLabExport", "BtnLabTweakBefore", "BtnLabTweakApply", "BtnLabTweakRevert" })
                Assert.NotNull(win.FindName(button));
        });
    }

    [Fact]
    public void Minimal_combo_shows_the_display_member_of_the_selected_item()
    {
        OnSta(() =>
        {
            EnsureTheme();
            var combo = new ComboBox
            {
                Style = (Style)Application.Current.FindResource("MinimalCombo"),
                DisplayMemberPath = nameof(Tweak.Title),
                ItemsSource = new[] { new Tweak { Title = "Agendamento de GPU" } },
                SelectedIndex = 0,
            };
            combo.Measure(new Size(600, 100));
            combo.Arrange(new Rect(0, 0, 600, 100));
            combo.UpdateLayout();

            static IEnumerable<TextBlock> Texts(DependencyObject o)
            {
                if (o is TextBlock t) yield return t;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
                    foreach (var hit in Texts(VisualTreeHelper.GetChild(o, i))) yield return hit;
            }

            Assert.Contains(Texts(combo), t => t.Text == "Agendamento de GPU");
            Assert.DoesNotContain(Texts(combo), t => t.Text.Contains("ThgOptimization"));
        });
    }

    [Fact]
    public void Color_order_dropdown_follows_the_advanced_switch()
    {
        OnSta(() =>
        {
            const string Xaml = """
                <ItemsControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Tag="{x:Static Visibility.Collapsed}">
                  <ItemsControl.ItemTemplate>
                    <DataTemplate>
                      <ComboBox Visibility="{Binding Tag, RelativeSource={RelativeSource AncestorType=ItemsControl}}"/>
                    </DataTemplate>
                  </ItemsControl.ItemTemplate>
                </ItemsControl>
                """;
            var list = (ItemsControl)XamlReader.Parse(Xaml);
            list.ItemsSource = new[] { "aparelho" };
            list.Measure(new Size(400, 200));
            list.Arrange(new Rect(0, 0, 400, 200));
            list.UpdateLayout();

            static ComboBox? Find(DependencyObject o)
            {
                if (o is ComboBox c) return c;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
                    if (Find(VisualTreeHelper.GetChild(o, i)) is { } hit) return hit;
                return null;
            }

            var combo = Find(list);
            Assert.NotNull(combo);
            Assert.Equal(Visibility.Collapsed, combo!.Visibility);
            list.Tag = Visibility.Visible;
            Assert.Equal(Visibility.Visible, combo.Visibility);
        });
    }

    [Fact]
    public void Tweak_applied_badge_follows_the_switch()
    {
        var t = new Tweak();
        var changed = new List<string?>();
        t.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("", t.AppliedBadge);
        t.IsOn = true;
        Assert.Equal("APLICADO", t.AppliedBadge);
        Assert.Contains(nameof(Tweak.AppliedBadge), changed);
    }

    [Theory]
    [InlineData("RGB", 1, 2, 3)]
    [InlineData("GRB", 2, 1, 3)]
    [InlineData("BRG", 3, 1, 2)]
    [InlineData("RBG", 1, 3, 2)]
    [InlineData("GBR", 2, 3, 1)]
    [InlineData("BGR", 3, 2, 1)]
    public void Light_mapper_follows_the_chosen_order(string order, byte a, byte b, byte c)
    {
        var vm = new LightDeviceVm();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Order = order;
        Assert.Equal((a, b, c), vm.Mapper(((byte)1, (byte)2, (byte)3)));
        Assert.Contains(nameof(LightDeviceVm.Order), changed);
    }
}
