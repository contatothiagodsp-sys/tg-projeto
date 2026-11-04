using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace ThgOptimization.Tests;

/// <summary>P1 a P6: desfazer no aviso, copiar cartão, título da sessão, todas as páginas montam, versão só num lugar, registro sempre com área.</summary>
[Collection(WpfCollection.Name)]
public class PolishTests
{
    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(RepoText.Root, "*.cs", SearchOption.AllDirectories).Where(p =>
        {
            string rel = Path.GetRelativePath(RepoText.Root, p);
            return !rel.StartsWith("tests", StringComparison.Ordinal) && !rel.StartsWith("bin", StringComparison.Ordinal) && !rel.StartsWith("obj", StringComparison.Ordinal) && !rel.StartsWith("dist", StringComparison.Ordinal);
        });

    // P1
    [Fact]
    public void Desfazer_do_aviso_roda_uma_vez_so()
    {
        int runs = 0;
        var undo = new ToastUndo();
        Assert.False(undo.Available);
        undo.Set(() => runs++);
        Assert.True(undo.Available);
        undo.Invoke();
        undo.Invoke();
        Assert.Equal(1, runs);
        Assert.False(undo.Available);
    }

    [Fact]
    public void Aviso_novo_ou_prazo_vencido_tira_o_desfazer()
    {
        int runs = 0;
        var undo = new ToastUndo();
        undo.Set(() => runs++);
        undo.Clear();
        undo.Invoke();
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Aviso_com_desfazer_mostra_o_botao_e_o_outro_nao()
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            var toast = new ToastWindow();
            try
            {
                toast.Notify("t", "x", "", () => { });
                Assert.Equal(Visibility.Visible, toast.UndoVisibility);
                toast.Notify("t", "x", "");
                Assert.Equal(Visibility.Collapsed, toast.UndoVisibility);
            }
            finally { toast.Close(); }
        });
    }

    /// <summary>Apagar arquivo não tem volta: nenhuma limpeza pode oferecer desfazer.</summary>
    [Fact]
    public void Limpeza_nao_promete_desfazer()
    {
        foreach (string name in new[] { "MainWindow.Clean.cs", "MainWindow.Ddu.cs" })
            Assert.DoesNotContain("ToastWithUndo", File.ReadAllText(Path.Combine(RepoText.Root, name)));
    }

    // P2
    [Fact]
    public void Copiar_cartao_gera_markdown_sem_botoes_nem_icones()
    {
        XamlSmokeTests.OnSta(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "CPU" });
            panel.Children.Add(new TextBlock { Text = "" });
            panel.Children.Add(new TextBlock { Text = "53 °C" });
            panel.Children.Add(new TextBlock { Text = "escondido", Visibility = Visibility.Collapsed });
            panel.Children.Add(new Button { Content = new TextBlock { Text = "CORRIGIR" } });
            panel.Measure(new Size(400, 400));
            panel.Arrange(new Rect(0, 0, 400, 400));
            panel.UpdateLayout();

            var lines = CardText.Collect(panel);
            Assert.Equal(new[] { "CPU", "53 °C" }, lines);
            Assert.Equal("### CPU\n- 53 °C\n", CardText.ToMarkdown(lines));
            Assert.Equal("", CardText.ToMarkdown(Array.Empty<string>()));
        });
    }

    // P3
    [Fact]
    public void Titulo_da_sessao_tem_jogo_e_data_e_o_jogo_continua_agrupando()
    {
        var s = new GameSessionRecord(new DateTime(2026, 11, 3, 20, 5, 0), "CS2", 30, 100, 70, false);
        Assert.Equal("CS2 · 2026-11-03 20:05", s.Title);
        Assert.Equal("CS2", s.Game); // o "antes e depois" e a medição do timer agrupam por este nome
        Assert.DoesNotContain("Title", JsonSerializer.Serialize(s));
    }

    // P4
    public static IEnumerable<object[]> Pages() =>
        Regex.Matches(File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml")), @"x:Name=""(Page[A-Z]\w*)""")
             .Select(m => new object[] { m.Groups[1].Value }).Distinct(new FirstItem());

    private sealed class FirstItem : IEqualityComparer<object[]>
    {
        public bool Equals(object[]? a, object[]? b) => Equals(a?[0], b?[0]);
        public int GetHashCode(object[] o) => o[0].GetHashCode();
    }

    /// <summary>Um parse só: o XAML da janela é enorme, e repeti-lo por página tirava tempo dos testes de relógio que rodam em paralelo.</summary>
    [Fact]
    public void Toda_pagina_monta_e_faz_layout()
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/tHgOptimization;component/") };
            var win = (Window)XamlReader.Parse(XamlSmokeTests.Loose(File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml"))), context);
            foreach (object[] item in Pages())
            {
                string name = (string)item[0];
                var page = Assert.IsAssignableFrom<FrameworkElement>(win.FindName(name));
                ((Panel)VisualOrLogicalParent(page)!).Children.Remove(page);
                page.Visibility = Visibility.Visible;
                var host = new Border { Child = page };
                host.Measure(new Size(1060, 760));
                host.Arrange(new Rect(0, 0, 1060, 760));
                host.UpdateLayout();
                Assert.True(page.ActualWidth > 0, $"{name} não fez layout.");
            }
        });
    }

    private static DependencyObject? VisualOrLogicalParent(FrameworkElement e) => e.Parent;

    [Fact]
    public void Toda_pagina_esta_na_lista_de_paginas_da_janela()
    {
        string code = File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml.cs"));
        foreach (var page in Pages()) Assert.Contains((string)page[0] + ",", code.Replace(" })", ", })"));
    }

    // P5
    [Fact]
    public void Versao_nao_fica_escrita_no_codigo()
    {
        var version = new Regex(@"\b1\.[78]\.\d+\b");
        // AppLog.cs: as chaves das novidades de cada versão. AppUpdater.cs: o número de transição da mudança de numeração.
        string[] allowed = { "AppLog.cs", "AppUpdater.cs" };
        foreach (string file in SourceFiles().Where(f => !allowed.Contains(Path.GetFileName(f))).Concat(new[] { Path.Combine(RepoText.Root, "MainWindow.xaml") }))
        {
            var hit = version.Match(File.ReadAllText(file));
            Assert.False(hit.Success, $"{Path.GetFileName(file)} escreve a versão à mão ({hit.Value}): ela vem só do .csproj.");
        }
    }

    // P6
    [Fact]
    public void Todo_registro_diz_a_area()
    {
        var call = new Regex(@"AppLog\.\w+\(\s*""([^""]*)""");
        foreach (string file in SourceFiles())
            foreach (Match m in call.Matches(File.ReadAllText(file)))
                Assert.False(string.IsNullOrWhiteSpace(m.Groups[1].Value), $"{Path.GetFileName(file)}: {m.Value} sem área.");
    }
}
