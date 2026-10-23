using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ThgOptimization.UI.Services;

/// <summary>
/// Retorno visual e acessibilidade para todos os botões do app, sem mexer em cada página:
/// - Botão que se desabilita no próprio clique (o padrão das ações demoradas daqui) ganha um indicador girando até voltar.
/// - Botão só de ícone ganha o nome para o leitor de tela a partir da dica (ToolTip).
/// </summary>
public static class UiFeedback
{
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Button), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase b || AutomationProperties.GetName(b).Length > 0) return;
        // Ícone da Segoe Fluent Icons (área de uso privado) não diz nada ao leitor de tela: usa a dica.
        if (b.Content is string { Length: 1 } glyph && glyph[0] >= '' && glyph[0] <= '' && b.ToolTip is string tip)
            AutomationProperties.SetName(b, tip);
        else if (b.Content is not string && b.ToolTip is string tip2)
            AutomationProperties.SetName(b, tip2);
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        // Depois do trecho síncrono do clique (até o primeiro await). Some ações rápidas sem piscar: só aparece após 300 ms.
        var t = new DispatcherTimer(DispatcherPriority.Background, b.Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!b.IsEnabled && b.IsVisible) Attach(b);
        };
        t.Start();
    }

    private static void Attach(Button b)
    {
        var layer = AdornerLayer.GetAdornerLayer(b);
        if (layer == null || layer.GetAdorners(b)?.OfType<SpinnerAdorner>().Any() == true) return;
        var a = new SpinnerAdorner(b);
        layer.Add(a);
        AutomationProperties.SetItemStatus(b, "Em andamento");

        DependencyPropertyChangedEventHandler? changed = null;
        RoutedEventHandler? unloaded = null;
        void Done()
        {
            layer.Remove(a);
            AutomationProperties.SetItemStatus(b, "");
            b.IsEnabledChanged -= changed;
            b.IsVisibleChanged -= changed;
            b.Unloaded -= unloaded;
        }
        changed = (_, _) => { if (b.IsEnabled || !b.IsVisible) Done(); };
        unloaded = (_, _) => Done();
        b.IsEnabledChanged += changed;
        b.IsVisibleChanged += changed;
        b.Unloaded += unloaded;
    }

    /// <summary>Arco girando no canto direito do botão (14 px, cor do texto do botão).</summary>
    private sealed class SpinnerAdorner : Adorner
    {
        private readonly Path _arc;

        public SpinnerAdorner(Button b) : base(b)
        {
            IsHitTestVisible = false;
            var rotate = new RotateTransform(0, 7, 7);
            _arc = new Path
            {
                Width = 14, Height = 14, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 7,1 A 6,6 0 1 1 1,7"), RenderTransform = rotate,
            };
            _arc.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = b });
            AddVisualChild(_arc);
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9))) { RepeatBehavior = RepeatBehavior.Forever });
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _arc;

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = AdornedElement.RenderSize;
            _arc.Arrange(new Rect(Math.Max(0, size.Width - 22), (size.Height - 14) / 2, 14, 14));
            return finalSize;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _arc.Measure(new Size(14, 14));
            return AdornedElement.RenderSize;
        }
    }
}
