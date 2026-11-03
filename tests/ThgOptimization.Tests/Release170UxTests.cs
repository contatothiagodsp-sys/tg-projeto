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
