using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThgOptimization.Views;

namespace ThgOptimization.Tests;

[Collection(WpfCollection.Name)]
public class FanCurveEditorTests
{
    private static FanCurveModel Model() => new(new (double, double)[] { (30, 20), (50, 40), (70, 80) });

    [Fact]
    public void Adicionar_ponto_no_meio_mantem_ordem()
    {
        var m = Model();
        Assert.Equal(2, m.Add(60.4, 55.6));
        Assert.Equal(new (double, double)[] { (30, 20), (50, 40), (60, 56), (70, 80) }, m.Points);
    }

    [Fact]
    public void Adicionar_em_cima_de_outro_ponto_nao_duplica()
    {
        var m = Model();
        Assert.Equal(-1, m.Add(50.2, 90));
        Assert.Equal(3, m.Points.Count);
    }

    [Fact]
    public void Remover_mantem_minimo_2()
    {
        var m = Model();
        Assert.True(m.Remove(1));
        Assert.False(m.Remove(0));
        Assert.False(m.Remove(5));
        Assert.Equal(2, m.Points.Count);
    }

    [Fact]
    public void Arrastar_respeita_limites()
    {
        var m = Model();
        m.Move(1, 999, 999); // não atravessa o vizinho da direita, nem passa de 100%
        Assert.Equal((69d, 100d), m.Points[1]);
        m.Move(1, -5, -5);
        Assert.Equal((31d, 0d), m.Points[1]);
        m.Move(2, 999, 50);
        Assert.Equal((110d, 50d), m.Points[2]);
        m.Move(0, -20, 10);
        Assert.Equal((0d, 10d), m.Points[0]);
    }

    [Fact]
    public void Salvar_reconstroi_formato_atual()
    {
        var m = Model();
        m.Add(60, 55);
        string text = m.ToText();
        Assert.Equal("30:20, 50:40, 60:55, 70:80", text);
        Assert.Equal(new (double, double)[] { (30, 20), (50, 40), (60, 55), (70, 80) }, FanCurves.ParseCustom(text));
    }

    [Fact]
    public void Formato_nao_muda_com_o_idioma_do_windows()
    {
        var m = new FanCurveModel(new (double, double)[] { (30.5, 20.5), (40, 30) });
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
            Assert.Equal("30.5:20.5, 40:30", m.ToText());
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Carregar_preset_nao_altera()
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            var editor = new FanCurveEditor();
            int edits = 0;
            editor.CurveEdited += _ => edits++;
            editor.Load(FanCurves.Curve(new FanChannelSettings { Preset = "silent" }));
            Assert.Equal(0, edits);
        });
    }

    [Fact]
    public void Editor_monta_e_desenha_com_o_tema_real()
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            var editor = new FanCurveEditor();
            editor.SetLive(55, "Agora: 55 °C · curva pede 40 %");
            editor.SetLive(null, "Sem leitura de temperatura");
            editor.SetTesting(true);
            editor.SetTesting(false);
            editor.Measure(new Size(700, 400));
            editor.Arrange(new Rect(0, 0, 700, 400));
            var bitmap = new RenderTargetBitmap(700, 400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(editor);
        });
    }
}
