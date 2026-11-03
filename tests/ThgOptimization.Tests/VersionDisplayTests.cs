using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ThgOptimization.Tests;

/// <summary>A versão é um número só: o do .csproj, igual em todos os lugares.</summary>
public class VersionDisplayTests
{
    private static string Repo(params string[] path)
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "tHgOptimization.csproj"))) dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(new[] { dir }.Concat(path).ToArray()));
    }

    private static string ProjectVersion() =>
        XDocument.Parse(Repo("tHgOptimization.csproj")).Descendants("Version").Single().Value;

    /// <summary>O regex das versões até a 1.7.37, que já estão instaladas e não mudam mais: primeiro número do título.</summary>
    private static Version LegacyParse(string title) => new(Regex.Match(title, @"\d+\.\d+\.\d+").Value);

    [Fact]
    public void Project_version_has_three_numbers()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", ProjectVersion());
    }

    [Fact]
    public void Running_version_is_the_project_version()
    {
        Assert.Equal(ProjectVersion(), AppUpdater.Current.ToString());
    }

    [Fact]
    public void Each_version_has_its_changelog_section()
    {
        Assert.Contains($"## [{ProjectVersion()}]", Repo("CHANGELOG.md"));
    }

    [Fact]
    public void Update_messages_name_the_real_version()
    {
        var next = new Version(1, 7, 5);
        Assert.Equal("Atualizar para a versão 1.7.5", AppUpdater.ActionLabel(next));
        Assert.Equal("Atualização 1.7.5 disponível", AppUpdater.AvailableLabel(next));
        Assert.Equal("A versão 1.7.5 está disponível.", AppUpdater.AvailableMessage(next));
    }

    [Fact]
    public void Exe_properties_do_not_carry_the_commit_hash()
    {
        string version = typeof(AppUpdater).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        Assert.Equal(ProjectVersion(), version);
    }

    [Theory]
    [InlineData("tHg Optimization 1.7.4", "1.7.4")]
    [InlineData("compat 1.7.9999 - tHg Optimization 1.7.4", "1.7.4")]
    [InlineData("tHg Optimization 1.7.38", "1.7.38")]
    public void New_apps_read_the_real_version_from_the_title(string title, string expected)
    {
        Assert.Equal(new Version(expected), AppUpdater.ParseVersion("latest", title));
    }

    /// <summary>Sem o número de transição, quem está na 1.7.37 nunca receberia a 1.7.4, porque 1.7.4 é menor que 1.7.37.</summary>
    [Fact]
    public void Installed_counter_builds_get_the_update_through_the_transition_number()
    {
        var newest = new Version(1, 7, 37);
        Assert.True(new Version(1, 7, 4) < newest);
        Assert.True(LegacyParse("compat 1.7.9999 - tHg Optimization 1.7.4") > newest);
    }

    [Fact]
    public void Updated_app_is_not_offered_its_own_version_again()
    {
        var parsed = AppUpdater.ParseVersion("latest", "compat 1.7.9999 - tHg Optimization 1.7.4");
        Assert.False(parsed > new Version(1, 7, 4));
    }

    [Fact]
    public void Workflow_has_no_second_version_number()
    {
        string workflow = Repo(".github", "workflows", "publicar.yml");
        Assert.DoesNotContain("GITHUB_RUN_NUMBER", workflow);
        Assert.DoesNotContain("(build", workflow);
        Assert.Contains("$titulo = \"tHg Optimization $env:APPVER\"", workflow);
        Assert.Contains("-le [version]'1.7.37') { $titulo = \"compat 1.7.9999 - $titulo\" }", workflow);
        Assert.Contains("--title $titulo", workflow);
    }

    [Fact]
    public void Workflow_does_not_publish_the_same_version_twice()
    {
        string workflow = Repo(".github", "workflows", "publicar.yml");
        Assert.Contains("$atual.EndsWith(\"tHg Optimization $ver\")", workflow);
        Assert.Contains("env.PUBLICAR == 'true' && startsWith(github.ref, 'refs/heads/')", workflow);
    }

    [Fact]
    public void Installer_shows_the_full_version()
    {
        string installer = Repo("installer", "tHgOptimization.iss");
        Assert.DoesNotContain("ShortVersion", installer);
        Assert.Contains("AppVersion={#AppVersion}", installer);
    }
}
