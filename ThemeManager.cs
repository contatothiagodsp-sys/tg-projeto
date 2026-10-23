using System.Windows;
using System.Windows.Media;

namespace ThgOptimization;

/// <summary>
/// Troca entre modo escuro e claro substituindo os pincéis da paleta (Theme.xaml). O XAML os referencia com DynamicResource,
/// então a troca vale na hora, sem recarregar janelas. Overlay e aviso de dispositivo ficam sempre escuros (aparecem sobre jogos).
/// </summary>
public static class ThemeManager
{
    public static bool IsLight { get; private set; }

    private static readonly (object Key, string Dark, string Light)[] Palette =
    {
        ("Bg", "#000000", "#FFFFFF"),
        ("Panel", "#080808", "#F2F2F2"),
        ("Card", "#0E0E0E", "#FFFFFF"),
        ("Hover", "#181818", "#E4E4E4"),
        ("Line", "#1F1F1F", "#CFCFCF"),
        ("LineStrong", "#3A3A3A", "#8A8A8A"),
        ("Fg", "#F5F5F5", "#111111"),
        ("Muted", "#8C8C8C", "#3F3F3F"),
        ("Faint", "#4A4A4A", "#5C5C5C"),
        ("Ink", "#000000", "#FFFFFF"),
        ("ThumbBrush", "#2E2E2E", "#CFCFCF"),
        ("ThumbHover", "#6A6A6A", "#A5A5A5"),
        ("ThumbDrag", "#9A9A9A", "#7A7A7A"),
        // Marca (azul da arte): botões principais, interruptores ligados, item selecionado do menu
        ("Accent", "#1F7BFF", "#0F62E6"),
        ("AccentSoft", "#0F2340", "#E3EEFF"),
        ("OnAccent", "#FFFFFF", "#FFFFFF"),
        // Estado: bom / atenção / ruim (texto e fundo suave das etiquetas)
        ("Good", "#3DDC97", "#0E8A55"),
        ("GoodSoft", "#0E2A1E", "#E3F6EC"),
        ("Warn", "#F5B942", "#A86400"),
        ("WarnSoft", "#2D2410", "#FBF0DC"),
        ("Bad", "#FF6B6B", "#C9302C"),
        ("BadSoft", "#331516", "#FBE4E3"),
        (SystemColors.ControlBrushKey, "#000000", "#FFFFFF"),
        (SystemColors.HighlightBrushKey, "#181818", "#E4E4E4"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "#181818", "#E4E4E4"),
    };

    public static void Apply(bool light)
    {
        IsLight = light;
        var res = Application.Current.Resources;
        foreach (var (key, dark, lightHex) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(light ? lightHex : dark);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush; // o XAML usa DynamicResource: trocar o pincel atualiza todos os controles na hora
        }
    }
}
