using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThgOptimization.UI;

/// <summary>Leva a tela até um item de uma lista. Com virtualização, o item pode ainda não existir na tela, e é preciso pedir ao painel que o crie.</summary>
internal static class ListFocus
{
    /// <summary>O elemento do item, já criado e dentro da área visível da lista. Null = o item não está na lista.</summary>
    public static FrameworkElement? Realize(ItemsControl list, object item)
    {
        int index = list.Items.IndexOf(item);
        if (index < 0) return null;
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement)
        {
            FindChild<VirtualizingPanel>(list)?.BringIndexIntoViewPublic(index);
            list.UpdateLayout();
        }
        return list.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
    }

    public static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is T deeper) return deeper;
        }
        return null;
    }
}
