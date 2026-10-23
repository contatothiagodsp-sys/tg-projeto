using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ThgOptimization;

/// <summary>
/// Ícone na bandeja do sistema (ao lado do relógio) pela API do Windows (Shell_NotifyIcon), sem depender do Windows Forms.
/// Clique: abre o app. Botão direito: menu. Volta sozinho se o Explorer reiniciar.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1;
    private const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;
    private string _tip;
    private bool _added, _v4;
    private DateTime _menuClosed;

    /// <summary>Item do menu nativo da bandeja (Id 0 = separador).</summary>
    public sealed record MenuEntry(int Id, string Text, bool Checked = false, bool Default = false, string Shortcut = "");

    public event Action? Click;
    public event Action? RightClick;

    /// <summary>Janela invisível que recebe as mensagens do ícone (precisa ficar em primeiro plano para o menu fechar direito).</summary>
    public IntPtr Handle => _window.Handle;

    public TrayIcon(string tip)
    {
        _tip = tip;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window = new HwndSource(new HwndSourceParameters("tHgTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(Hook);
        _icon = LoadAppIcon();
        Add();
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            var small = new IntPtr[1];
            if (ExtractIconEx(Environment.ProcessPath!, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero) return small[0];
        }
        catch { }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    private NOTIFYICONDATA Data(int flags)
    {
        var d = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _window.Handle, uID = 1, uFlags = flags,
            uCallbackMessage = WM_APP_TRAY, hIcon = _icon, szTip = _tip, uTimeoutOrVersion = 4,
        };
        return d;
    }

    private void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref d);
        if (_added) _v4 = Shell_NotifyIcon(NIM_SETVERSION, ref d);
    }

    public void SetTip(string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        if (!_added) return;
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = lParam.ToInt32() & 0xFFFF; // NOTIFYICON_VERSION_4: o evento vem na parte baixa
            if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) Click?.Invoke();
            // Versão 4 manda WM_CONTEXTMENU (botão direito ou teclado); sem ela, WM_RBUTTONUP. Nunca os dois, senão o menu abre duas vezes.
            else if ((_v4 ? ev == WM_CONTEXTMENU : ev == WM_RBUTTONUP) && (DateTime.Now - _menuClosed).TotalMilliseconds > 300) RightClick?.Invoke();
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0) Add(); // o Explorer reiniciou: o ícone some e precisa voltar
        return IntPtr.Zero;
    }

    /// <summary>
    /// Menu nativo do Windows (o mesmo dos outros ícones da bandeja): funciona também dentro da área de ícones ocultos (^),
    /// onde um menu WPF fecha sozinho ao abrir. Segue o tema claro/escuro do Windows. Devolve o Id escolhido ou 0.
    /// </summary>
    public int ShowMenu(IEnumerable<MenuEntry> items)
    {
        TryDarkMenus();
        IntPtr menu = CreatePopupMenu();
        try
        {
            foreach (var it in items)
            {
                if (it.Id == 0) { AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null); continue; }
                string text = it.Shortcut.Length > 0 ? $"{it.Text}\t{it.Shortcut}" : it.Text;
                AppendMenu(menu, MF_STRING | (it.Checked ? MF_CHECKED : 0), (IntPtr)it.Id, text);
                if (it.Default) SetMenuDefaultItem(menu, (uint)it.Id, 0);
            }
            GetCursorPos(out var pt);
            SetForegroundWindow(_window.Handle); // sem isso o menu não fecha ao clicar fora
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_BOTTOMALIGN, pt.X, pt.Y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL: recomendação da Microsoft para o menu fechar direito
            return cmd;
        }
        finally
        {
            DestroyMenu(menu);
            _menuClosed = DateTime.Now;
        }
    }

    private static bool _darkTried;

    /// <summary>Menus em modo escuro quando o Windows está no escuro (API do uxtheme usada pelo Explorer e Bloco de Notas).</summary>
    private static void TryDarkMenus()
    {
        if (_darkTried) return;
        _darkTried = true;
        try
        {
            if (Environment.OSVersion.Version.Build < 18362) return;
            SetPreferredAppMode(1); // AllowDark: segue o tema do sistema
            FlushMenuThemes();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_added) { var d = Data(0); Shell_NotifyIcon(NIM_DELETE, ref d); _added = false; }
        _window.RemoveHook(Hook);
        _window.Dispose();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_BOTTOMALIGN = 0x20;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPos);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int count);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
