using System.IO;
using Xunit;

namespace ThgOptimization.Tests;

public class VersionDisplayTests
{
    private static readonly Version Installed = new(1, 6, 31);

    [Theory]
    [InlineData("1.6.31", "1.6")]
    [InlineData("1.6.0", "1.6")]
    [InlineData("12.10.5", "12.10")]
    public void Display_shows_only_major_and_minor(string full, string shown)
    {
        Assert.Equal(shown, AppUpdater.Display(new Version(full)));
    }

    [Fact]
    public void Current_display_has_two_parts()
    {
        Assert.Equal(2, AppUpdater.CurrentDisplay.Split('.').Length);
    }

    [Fact]
    public void Update_inside_the_same_version_does_not_offer_the_version_you_already_have()
    {
        var next = new Version(1, 6, 32);
        Assert.Equal("Instalar atualização", AppUpdater.ActionLabel(Installed, next));
        Assert.Equal("Atualização disponível", AppUpdater.AvailableLabel(Installed, next));
        Assert.Contains("atualização da versão 1.6", AppUpdater.AvailableMessage(Installed, next));
    }

    [Fact]
    public void Update_to_a_new_version_names_it_without_the_build_number()
    {
        var next = new Version(1, 7, 40);
        Assert.Equal("Atualizar para a versão 1.7", AppUpdater.ActionLabel(Installed, next));
        Assert.Equal("Atualização 1.7 disponível", AppUpdater.AvailableLabel(Installed, next));
        Assert.DoesNotContain("40", AppUpdater.AvailableMessage(Installed, next));
    }

    /// <summary>A 1.6.31 já instalada usa este mesmo regex: se o título perder o número completo, ela nunca mais atualiza.</summary>
    [Fact]
    public void Installed_apps_still_find_the_full_version_in_the_new_title()
    {
        var parsed = AppUpdater.ParseVersion("latest", "tHg Optimization 1.6 (build 1.6.32)");
        Assert.Equal(new Version(1, 6, 32), parsed);
        Assert.True(parsed > Installed);
    }

    [Fact]
    public void Workflow_title_keeps_the_full_version()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "tHgOptimization.csproj"))) dir = Path.GetDirectoryName(dir)!;
        string workflow = File.ReadAllText(Path.Combine(dir, ".github", "workflows", "publicar.yml"));
        Assert.Contains("gh release create latest", workflow);
        Assert.Contains("--title \"tHg Optimization $short (build $env:APPVER)\"", workflow);
    }
}
