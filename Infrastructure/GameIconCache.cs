using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Extrai e guarda em cache o ícone congelado de executáveis de jogos para exibição rápida na lista de perfis,
/// sem reabrir arquivos repetidamente e sem vazar alças GDI.
/// </summary>
public static class GameIconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return Cache.GetOrAdd(exePath, Extract);
    }

    private static ImageSource? Extract(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            uint count = ExtractIconEx(path, 0, large, small, 1);
            if (count == 0) return null;
            IntPtr hIcon = small[0] != IntPtr.Zero ? small[0] : large[0];
            try
            {
                if (hIcon == IntPtr.Zero) return null;
                var bmp = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or NotSupportedException)
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
