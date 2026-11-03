using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ThgOptimization.UI;

/// <summary>
/// Lista com rolagem própria dentro de uma página que também rola. O ScrollViewer marca a roda do mouse como tratada mesmo já no fim,
/// e a página parava de rolar com o ponteiro em cima da lista. Com isto, no limite da lista, a roda segue para a página.
/// </summary>
public static class ScrollChain
{
    public static readonly DependencyProperty ForwardAtEdgesProperty = DependencyProperty.RegisterAttached(
        "ForwardAtEdges", typeof(bool), typeof(ScrollChain), new PropertyMetadata(false, OnChanged));

    public static bool GetForwardAtEdges(DependencyObject o) => (bool)o.GetValue(ForwardAtEdgesProperty);
    public static void SetForwardAtEdges(DependencyObject o, bool value) => o.SetValue(ForwardAtEdgesProperty, value);

    /// <summary>Sem nada a rolar, ou já no limite nessa direção, a roda é da página.</summary>
    internal static bool PassesToParent(double offset, double scrollable, int delta) =>
        delta > 0 ? offset <= 0 : offset >= scrollable;

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue) viewer.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer || !PassesToParent(viewer.VerticalOffset, viewer.ScrollableHeight, e.Delta)) return;
        if (LogicalOrVisualParent(viewer) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = viewer });
    }

    private static DependencyObject? LogicalOrVisualParent(FrameworkElement element) =>
        element.Parent ?? element.TemplatedParent ?? System.Windows.Media.VisualTreeHelper.GetParent(element);
}
