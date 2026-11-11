using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThgOptimization;

/// <summary>Resultado de <see cref="Unelevated.TryStart"/>: ou o programa abriu sem administrador, ou não abriu (com o motivo).</summary>
public readonly record struct UnelevatedResult(bool Started, Process? Process, string? Error)
{
    public static UnelevatedResult Ok(Process? p) => new(true, p, null);
    public static UnelevatedResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Abre um programa SEM privilégio de administrador, com o mesmo token da Área de Trabalho (Explorer), mesmo o tHg rodando
/// elevado. Jogos e programas auxiliares não devem herdar administrador: é o que launchers como Steam esperam, evita
/// que um programa comprometido ganhe o PC inteiro e mantém arrastar-e-soltar e atalhos funcionando entre janelas.
/// É o método recomendado pela Microsoft (token do shell + CreateProcessWithTokenW).
/// Se não der para garantir o token comum, NÃO abre: abrir "do jeito normal" a partir do tHg daria administrador ao programa.
/// Quem chama avisa o usuário, que continua podendo abrir o programa pelo próprio Windows.
/// </summary>
public static class Unelevated
{
    /// <summary>Costura de teste: a criação real do processo com o token do Explorer.</summary>
    internal static Func<string, string, string?, Process?> ShellTokenStarter = StartWithShellToken;

    /// <summary>Abre sem administrador ou devolve a falha. Nunca cai para uma abertura elevada.</summary>
    public static UnelevatedResult TryStart(string exe, string args, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(exe)) return UnelevatedResult.Fail("Nenhum programa informado.");
        try { return UnelevatedResult.Ok(ShellTokenStarter(exe, args ?? "", workingDir)); }
        catch (Exception ex)
        {
            // Só o nome do arquivo: caminho completo e argumentos podem trazer nome de usuário, contas e chaves.
            Infrastructure.AppLog.Warn("abrir-sem-admin", $"{SafeName(exe)}: {ex.GetType().Name}: {ex.Message}");
            return UnelevatedResult.Fail(Explain(ex));
        }
    }

    internal static string SafeName(string exe)
    {
        string name = exe.Trim().Trim('"');
        name = name[(name.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return name.Length > 0 ? name : "programa";
    }

    internal static string Explain(Exception ex) => ex switch
    {
        ShellUnavailableException => "O Explorer do Windows não está em execução, e sem ele o tHg não consegue abrir o programa sem " +
                                     "privilégio de administrador. Abra o programa pelo Windows ou reinicie o Explorer.",
        Win32Exception w => $"O Windows recusou abrir o programa sem privilégio de administrador ({w.Message.TrimEnd('.')}). " +
                            "Abra o programa pelo Windows.",
        _ => $"Não foi possível abrir o programa sem privilégio de administrador ({ex.Message.TrimEnd('.')}). Abra o programa pelo Windows.",
    };

    internal sealed class ShellUnavailableException() : InvalidOperationException("Sem Explorer");

    private static Process? StartWithShellToken(string exe, string args, string? workingDir)
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new ShellUnavailableException();
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
