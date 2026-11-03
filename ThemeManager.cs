using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ThgOptimization;

/// <summary>
/// Troca entre modo escuro e claro substituindo os pincéis da paleta (Theme.xaml). O XAML os referencia com DynamicResource,
/// então a troca vale na hora, sem recarregar janelas. Overlay e aviso de dispositivo ficam sempre escuros (aparecem sobre jogos).
/// No modo "system" segue o tema de apps do Windows e acompanha quando o usuário troca lá.
/// </summary>
public static class ThemeManager
{
    public const string System = "system", Dark = "dark", Light = "light";

    public static bool IsLight { get; private set; }
    public static string Mode { get; private set; } = System;

    /// <summary>Disparado depois de cada troca de paleta (para a janela atualizar a moldura e o botão).</summary>
    public static event Action? Changed;

    static ThemeManager()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Mode == System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(() => { if (SystemPrefersLight() != IsLight) Apply(!IsLight); });
        };
    }

    private static readonly (object Key, string Dark, string Light)[] Palette =
    {
        // Paleta gamer: Bg0–2 são os fundos, Border0–1 as bordas e Fg0–2 os textos. O claro mantém os fundos brancos e usa um
        // ciano mais escuro, porque #00E5FF sobre branco fica ilegível.
        ("Bg0Brush", "#0A0E14", "#FFFFFF"),
        ("Bg1Brush", "#11171F", "#FFFFFF"),
        ("Bg2Brush", "#1A2230", "#E4E4E4"),
        ("Border0Brush", "#1F2937", "#CFCFCF"),
        ("Border1Brush", "#2A3548", "#8A8A8A"),
        ("Fg0Brush", "#E5E7EB", "#111111"),
        ("Fg1Brush", "#9CA3AF", "#3F3F3F"),
        ("Fg2Brush", "#6B7280", "#5C5C5C"),
        ("AccentBrush", "#00E5FF", "#00798C"),
        ("AccentDimBrush", "#00B8D4", "#005F6E"),
        ("Accent2Brush", "#FF00E5", "#C000AD"),
        ("OkBrush", "#00FF88", "#0E8A55"),
        ("WarnBrush", "#FFB020", "#A86400"),
        ("DangerBrush", "#FF3860", "#C9302C"),
        ("AccentHoverBrush", "#2200E5FF", "#2200798C"),
        ("AccentTintBrush", "#1A00E5FF", "#1A00798C"),
        ("HeroGlow", "#0F3A47", "#D6F0F4"),
        // Nomes usados pelo restante do app (XAML e FindResource), mapeados para a paleta acima
        ("Bg", "#0A0E14", "#FFFFFF"),
        ("Panel", "#0A0E14", "#F2F2F2"),
        ("Card", "#11171F", "#FFFFFF"),
        ("Hover", "#1A2230", "#E4E4E4"),
        ("Line", "#1F2937", "#CFCFCF"),
        ("LineStrong", "#2A3548", "#8A8A8A"),
        ("Fg", "#E5E7EB", "#111111"),
        ("Muted", "#9CA3AF", "#3F3F3F"),
        ("Faint", "#6B7280", "#5C5C5C"),
        ("Ink", "#0A0E14", "#FFFFFF"),
        ("ThumbBrush", "#2A3548", "#CFCFCF"),
        ("ThumbHover", "#6B7280", "#A5A5A5"),
        ("ThumbDrag", "#9CA3AF", "#7A7A7A"),
        // Marca: botões principais, interruptores ligados, item selecionado do menu
        ("Accent", "#00E5FF", "#00798C"),
        ("AccentSoft", "#0A2A33", "#DDF3F6"),
        ("OnAccent", "#0A0E14", "#FFFFFF"),
        // Estado: bom / atenção / ruim (texto e fundo suave das etiquetas)
        ("Good", "#00FF88", "#0E8A55"),
        ("GoodSoft", "#06291C", "#E3F6EC"),
        ("Warn", "#FFB020", "#A86400"),
        ("WarnSoft", "#2E2210", "#FBF0DC"),
        ("Bad", "#FF3860", "#C9302C"),
        ("BadSoft", "#33121C", "#FBE4E3"),
        (SystemColors.ControlBrushKey, "#0A0E14", "#FFFFFF"),
        (SystemColors.HighlightBrushKey, "#1A2230", "#E4E4E4"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "#1A2230", "#E4E4E4"),
    };

    /// <summary>Modo escolhido pelo usuário: "system" (igual ao Windows), "dark" ou "light".</summary>
    public static void SetMode(string mode)
    {
        Mode = mode is Dark or Light ? mode : System;
        Apply(Mode == Light || Mode == System && SystemPrefersLight());
    }

    /// <summary>Tema de apps do Windows (Configurações → Personalização → Cores). Sem a chave: escuro, a cara do tHg.</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

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
        // Degradê dos botões principais: as duas pontas vêm da paleta do tema.
        var gradient = new LinearGradientBrush(((SolidColorBrush)res["AccentBrush"]).Color, ((SolidColorBrush)res["AccentDimBrush"]).Color, 90);
        gradient.Freeze();
        res["AccentGradient"] = gradient;
        // Cartão de destaque do painel: o acento sobe de baixo para cima até a cor normal de cartão.
        var hero = new LinearGradientBrush(((SolidColorBrush)res["HeroGlow"]).Color, ((SolidColorBrush)res["Card"]).Color, new Point(0, 1), new Point(0, 0));
        hero.Freeze();
        res["HeroGradient"] = hero;
        Changed?.Invoke();
    }
}
