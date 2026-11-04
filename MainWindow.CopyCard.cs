using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThgOptimization;

/// <summary>O texto de um cartão, como Markdown: o primeiro texto vira título, e o resto, itens. Botões ficam de fora, porque não são informação.</summary>
internal static class CardText
{
    public static List<string> Collect(DependencyObject root)
    {
        var lines = new List<string>();
        Walk(root, lines);
        return lines;
    }

    private static void Walk(DependencyObject node, List<string> lines)
    {
        if (node is Button) return;
        if (node is UIElement { Visibility: not Visibility.Visible }) return;
        if (node is TextBlock text)
        {
            string line = text.Text.Trim();
            if (line.Length > 0 && !IsIconGlyph(line)) lines.Add(line);
            return;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), lines);
    }

    /// <summary>Glifo da fonte de ícones (área de uso privado): na cópia, viraria um quadrado.</summary>
    private static bool IsIconGlyph(string text) => text.All(c => c is >= '' and <= '');

    public static string ToMarkdown(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return "";
        var sb = new StringBuilder("### ").Append(lines[0]).Append('\n');
        for (int i = 1; i < lines.Count; i++) sb.Append("- ").Append(lines[i]).Append('\n');
        return sb.ToString();
    }
}

public partial class MainWindow
{
    private void InitCopyCard() => PreviewKeyDown += (_, e) =>
    {
        if (e.Key != Key.C || Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;
        var card = FindCard(Keyboard.FocusedElement as DependencyObject) ?? FindCard(Mouse.DirectlyOver as DependencyObject);
        if (card == null) return;
        e.Handled = true;
        string text = CardText.ToMarkdown(CardText.Collect(card));
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            Toast("Cartão copiado", "O conteúdo está na área de transferência, em Markdown.", "");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Infrastructure.AppLog.Warn("janela", ex); // outro programa segurava a área de transferência
            Toast("Copiar cartão", "A área de transferência está ocupada. Tente de novo.", "");
        }
    };

    /// <summary>O cartão em volta do elemento: pelo foco, ou pelo que está sob o mouse.</summary>
    private Border? FindCard(DependencyObject? from)
    {
        var card = (Style)FindResource("CardBorder");
        var hero = (Style)FindResource("HeroCard");
        for (var node = from; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is Border b && (ReferenceEquals(b.Style, card) || ReferenceEquals(b.Style, hero))) return b;
        return null;
    }
}
