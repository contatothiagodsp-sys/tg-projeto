using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThgOptimization;

/// <summary>
/// Abre um programa SEM privilégio de administrador, com o mesmo token da Área de Trabalho (Explorer), mesmo o tHg rodando
/// elevado. Jogos e programas auxiliares não devem herdar administrador: é o que launchers como Steam esperam, evita
/// que um programa comprometido ganhe o PC inteiro e mantém arrastar-e-soltar e atalhos funcionando entre janelas.
/// É o método recomendado pela Microsoft (token do shell + CreateProcessWithTokenW). Se falhar, abre do jeito normal.
/// </summary>
public static class Unelevated
{
    public static Process? Start(string exe, string args, string? workingDir)
    {
        try { return StartWithShellToken(exe, args, workingDir); }
        catch { return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = workingDir ?? "" }); }
    }

    private static Process? StartWithShellToken(string exe, string args, string? workingDir)
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("Sem Explorer");
        GetWindowThreadProcessId(shell, out uint pid);
        IntPtr hProc = OpenProcess(0x0400 /* QUERY_INFORMATION */, false, pid);
        if (hProc == IntPtr.Zero) throw new Win32Exception();
        IntPtr shellToken = IntPtr.Zero, token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(hProc, 0x0002 /* DUPLICATE */, out shellToken)) throw new Win32Exception();
            const uint access = 0x0008 | 0x0001 | 0x0002 | 0x0080 | 0x0100; // QUERY | ASSIGN_PRIMARY | DUPLICATE | ADJUST_DEFAULT | ADJUST_SESSIONID
            if (!DuplicateTokenEx(shellToken, access, IntPtr.Zero, 2 /* Impersonation */, 1 /* Primary */, out token)) throw new Win32Exception();
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            string cmd = $"\"{exe}\"" + (string.IsNullOrWhiteSpace(args) ? "" : " " + args);
            if (!CreateProcessWithTokenW(token, 0, exe, cmd, 0, IntPtr.Zero, workingDir, ref si, out var pi)) throw new Win32Exception();
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            try { return Process.GetProcessById(pi.dwProcessId); } catch { return null; }
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            CloseHandle(hProc);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int level, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string app, string cmd, int flags, IntPtr env, string? dir,
                                                       ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
