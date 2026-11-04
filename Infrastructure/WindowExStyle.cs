using System.Runtime.InteropServices;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Estilo estendido de uma janela. Em 64 bits o Windows só exporta as versões Ptr; em 32 bits, só as outras.
/// Sem o Ptr em 64 bits, o valor passaria por um int e perderia a metade alta.
/// </summary>
internal static class WindowExStyle
{
    private const int GWL_EXSTYLE = -20;

    public static long Read(IntPtr hwnd) =>
        IntPtr.Size == 8 ? (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE) : GetWindowLongW(hwnd, GWL_EXSTYLE);

    /// <summary>Liga estes bits e mantém os demais.</summary>
    public static void Add(IntPtr hwnd, long flags)
    {
        long style = Read(hwnd) | flags;
        if (IntPtr.Size == 8) SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (IntPtr)style);
        else SetWindowLongW(hwnd, GWL_EXSTYLE, (int)style);
    }

    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
}
