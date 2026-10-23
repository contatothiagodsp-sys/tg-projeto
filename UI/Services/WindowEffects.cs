using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ThgOptimization.UI.Services;

/// <summary>
/// Acabamento nativo do Windows 11 para janelas sem a moldura padrão: cantos arredondados, borda fina na cor do tema e a
/// barra de título escura/clara do sistema (usada no Alt+Tab e nas miniaturas). No Windows 10 o DWM recusa os atributos
/// novos e a janela fica como sempre foi.
/// </summary>
public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    public static void Apply(Window w, bool light)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int dark = light ? 0 : 1;
        int round = DWMWCP_ROUND;
        int border = light ? 0x00CFCFCF : 0x001F1F1F; // COLORREF (0x00BBGGRR): mesma cor do pincel "Line"
        try
        {
            DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (DllNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
