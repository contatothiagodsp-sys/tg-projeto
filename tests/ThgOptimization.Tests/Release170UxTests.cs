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
