using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using ThgOptimization.Core.Oc;

namespace ThgOptimization.Tests;

internal static class RepoText
{
    public static string File(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (System.IO.File.Exists(Path.Combine(d.FullName, "tHgOptimization.csproj"))) return System.IO.File.ReadAllText(Path.Combine(d.FullName, name));
        throw new FileNotFoundException(name);
    }
}

public class HeroCardTests
{
    [Fact]
    public void Hover_keeps_the_gradient()
    {
        string theme = RepoText.File("Theme.xaml");
        int hero = theme.IndexOf("x:Key=\"HeroCard\"", StringComparison.Ordinal);
        string style = theme[hero..theme.IndexOf("</Style>", hero, StringComparison.Ordinal)];
        int trigger = style.IndexOf("IsMouseOver", StringComparison.Ordinal);
        Assert.True(trigger > 0);
        Assert.Contains("HeroGradient", style[trigger..]);
    }
}

public class ChipContrastTests
{
    private static double Luminance(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static (string Dark, string Light) Palette(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(RepoText.File("ThemeManager.cs"), "\\(\"" + name + "\", \"(#[0-9A-F]{6})\", \"(#[0-9A-F]{6})\"\\)");
        Assert.True(m.Success, name);
        return (m.Groups[1].Value, m.Groups[2].Value);
    }

    [Fact]
    public void Selected_chip_reaches_wcag_aa_in_both_themes()
    {
        var accent = Palette("Accent");
        var onAccent = Palette("OnAccent");
        Assert.True(Contrast(accent.Dark, onAccent.Dark) >= 4.5, $"Escuro: {Contrast(accent.Dark, onAccent.Dark):0.00}");
        Assert.True(Contrast(accent.Light, onAccent.Light) >= 4.5, $"Claro: {Contrast(accent.Light, onAccent.Light):0.00}");
    }

    [Fact]
    public void Selected_chip_uses_the_on_accent_color()
    {
        string theme = RepoText.File("Theme.xaml");
        int chip = theme.IndexOf("x:Key=\"Chip\"", StringComparison.Ordinal);
        string style = theme[chip..theme.IndexOf("</Style>", chip, StringComparison.Ordinal)];
        int selected = style.IndexOf("Property=\"IsChecked\"", StringComparison.Ordinal);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource OnAccent}\"", style[selected..]);
    }
}

public class CaptionButtonNameTests
{
    [Theory]
    [InlineData("Min_Click", "Minimizar")]
    [InlineData("Max_Click", "Maximizar")]
    [InlineData("Close_Click", "Fechar")]
    [InlineData("Theme_Click", "Alternar tema")]
    public void Screen_reader_gets_a_name(string click, string name)
    {
        string xaml = RepoText.File("MainWindow.xaml");
        int at = xaml.IndexOf($"Click=\"{click}\"", StringComparison.Ordinal);
        Assert.Contains($"AutomationProperties.Name=\"{name}\"", xaml[at..xaml.IndexOf('>', at)]);
    }
}

public class IntegOrderTests
{
    [Fact]
    public void Page_follows_connect_choose_control()
    {
        string xaml = RepoText.File("MainWindow.xaml");
        int page = xaml.IndexOf("x:Name=\"PageInteg\"", StringComparison.Ordinal);
        int integrations = xaml.IndexOf("Text=\"INTEGRAÇÕES\"", page, StringComparison.Ordinal);
        int devices = xaml.IndexOf("Text=\"DISPOSITIVOS CONECTADOS\"", page, StringComparison.Ordinal);
        int control = xaml.IndexOf("Text=\"CONTROLE GLOBAL\"", page, StringComparison.Ordinal);
        Assert.True(integrations < devices && devices < control);
    }
}

public class InputQueueTweakTests
{
    [Fact]
    public void Tweak_is_not_offered_to_anyone_who_has_not_applied_it()
    {
        Assert.False(WindowsTweaks.InputQueueOffered(alreadyApplied: false)); // inclui mouse de 8000 Hz
    }

    [Fact]
    public void Whoever_applied_it_can_still_undo_it()
    {
        Assert.True(WindowsTweaks.InputQueueOffered(alreadyApplied: true));
    }

    [Fact]
    public void Turbo_never_suggests_it()
    {
        Assert.Equal(0, WindowsTweaks.Definitions().Single(t => t.Id == "inputqueues").Weight);
    }
}

public class ExperimentalLabelTests
{
    [Fact]
    public void Store_apps_tweak_says_it_is_experimental()
    {
        Assert.StartsWith("[EXPERIMENTAL] ", WindowsTweaks.CleanupTweaks().Single(t => t.Id == "clean.bgapps").Title);
    }

    [Theory]
    [InlineData("Timer da plataforma")]
    [InlineData("desligar o ULPS")]
    public void Competitive_tweaks_with_no_evidence_say_so(string title)
    {
        Assert.Contains("\"[EXPERIMENTAL] ", RepoText.File("WindowsTweaks.Competitive.cs")[..RepoText.File("WindowsTweaks.Competitive.cs").IndexOf(title, StringComparison.Ordinal)][^40..]);
    }
}

public class OcClockStateTests
{
    private static readonly uint?[] Caps = { null, 2200, 2000, 1700 };
    private static readonly DateTime Boot = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, 0)]
    [InlineData(2200u, 1)]
    [InlineData(1700u, 3)]
    [InlineData(1234u, -1)]
    public void Chip_of_the_cap_in_use_is_selected(uint? current, int chip)
    {
        Assert.Equal(chip, OcClockState.SelectedChip(Caps, current));
    }

    [Fact]
    public void Cap_saved_in_this_boot_is_still_in_use()
    {
        Assert.Equal(2200u, OcClockState.RestoredCap(2200, Boot.AddMinutes(5), Boot));
    }

    [Fact]
    public void Cap_saved_before_the_last_boot_is_gone()
    {
        Assert.Null(OcClockState.RestoredCap(2200, Boot.AddDays(-1), Boot));
    }

    [Fact]
    public void Nothing_saved_means_no_cap()
    {
        Assert.Null(OcClockState.RestoredCap(null, null, Boot));
        Assert.Null(OcClockState.RestoredCap(2200, null, Boot));
    }
}
