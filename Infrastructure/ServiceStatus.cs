using System.Runtime.InteropServices;

namespace ThgOptimization.Infrastructure;

/// <summary>Estado de um serviço do Windows direto pelo Gerenciador de Serviços (sem abrir o sc.exe para um "query").</summary>
public static class ServiceStatus
{
    /// <summary>true = rodando, false = parado/em transição, null = serviço inexistente ou sem acesso.</summary>
    public static bool? IsRunning(string name)
    {
        IntPtr scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            IntPtr svc = OpenService(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return null;
            try { return QueryServiceStatus(svc, out var st) ? st.CurrentState == ServiceRunning : null; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004, ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
