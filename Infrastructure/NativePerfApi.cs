using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Chamadas nativas de desempenho usadas pelo Modo Jogo e pelo OTIMIZA: resolução do timer do sistema (ntdll), listas de
/// memória (Standby e Modified) e máscaras de núcleos lógicos. Nada aqui abre processos externos.
/// </summary>
public static class NativePerfApi
{
    /// <summary>0,5 ms em unidades de 100 ns (o mínimo da maioria dos PCs).</summary>
    public const uint HalfMs = 5000;

    /// <summary>Resolução do timer em unidades de 100 ns. Coarsest = a mais lenta (15,6 ms), Finest = a mais fina permitida.</summary>
    public readonly record struct TimerResolution(uint Coarsest, uint Finest, uint Current);

    public static TimerResolution? QueryTimer()
    {
        try { return NtQueryTimerResolution(out uint max, out uint min, out uint cur) == 0 ? new TimerResolution(max, min, cur) : null; }
        catch { return null; }
    }

    /// <summary>
    /// Pede (set = true) ou devolve (set = false) a resolução. O pedido vale só enquanto este processo existir: se o tHg
    /// fechar ou travar, o Windows desfaz sozinho. Devolve o valor em vigor depois da chamada, ou null se o Windows recusou.
    /// </summary>
    public static uint? SetTimer(uint desired, bool set)
    {
        try { return NtSetTimerResolution(desired, set, out uint cur) == 0 ? cur : null; }
        catch { return null; }
    }

    /// <summary>0,5 ms, ou o mais fino que este PC aceita se for maior que isso.</summary>
    public static uint GameTimer() => QueryTimer() is { Finest: > HalfMs } t ? t.Finest : HalfMs;

    private const int SystemMemoryListInformation = 80;
    private const int MemoryFlushModifiedList = 3, MemoryPurgeStandbyList = 4;

    /// <summary>
    /// Esvazia a memória em espera (Standby). Com <paramref name="flushModified"/>, antes grava no disco as páginas
    /// modificadas (Modified Page List), que passam para a espera e saem junto. Precisa de administrador.
    /// </summary>
    public static bool PurgeStandbyList(bool flushModified = false) => PurgeStandbyList(flushModified, out _);

    public static bool PurgeStandbyList(bool flushModified, out string? error)
    {
        error = null;
        try
        {
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            if (flushModified)
            {
                int flush = MemoryFlushModifiedList;
                NtSetSystemInformation(SystemMemoryListInformation, ref flush, sizeof(int)); // falhar aqui não impede a purga
            }
            int command = MemoryPurgeStandbyList;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            if (status != 0) error = $"o Windows recusou (0x{status:X8}); é preciso rodar como administrador";
            return status == 0;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Liga um privilégio que o token do processo já tem (administrador), mas que vem desligado.</summary>
    public static void EnablePrivilege(string name)
    {
        using var self = Process.GetCurrentProcess();
        if (!OpenProcessToken(self.Handle, 0x0020 | 0x0008, out var token)) // ADJUST | QUERY
            throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid)) throw new Win32Exception();
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = 2 }; // SE_PRIVILEGE_ENABLED
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Todos os núcleos lógicos do grupo 0 (onde ficam os processos comuns), até 64.</summary>
    public static ulong AllCoresMask()
    {
        int n = Math.Min(Environment.ProcessorCount, 64);
        return n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
    }

    /// <summary>Os dois últimos núcleos lógicos (os de fundo do Modo Jogo), ou null em CPUs com menos de 6 threads.</summary>
    public static ulong? LastTwoCoresMask()
    {
        int n = Math.Min(Environment.ProcessorCount, 64);
        return n < 6 ? null : (1UL << (n - 1)) | (1UL << (n - 2));
    }

    /// <summary>
    /// O primeiro núcleo físico (o thread lógico 0 e o irmão de SMT dele, se houver): onde o Windows atende a maior parte
    /// das interrupções e DPCs. null se a CPU não informar.
    /// </summary>
    public static unsafe ulong? FirstCoreMask()
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, null, ref len);
        if (len == 0) return null;
        byte* buf = (byte*)NativeMemory.Alloc(len);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return null;
            for (uint off = 0; off < len;)
            {
                byte* p = buf + off;
                uint size = *(uint*)(p + 4);
                // PROCESSOR_RELATIONSHIP: GroupMask@32 (Mask, Group@40)
                if (*(ushort*)(p + 40) == 0 && (*(ulong*)(p + 32) & 1) != 0) return *(ulong*)(p + 32);
                if (size == 0) break;
                off += size;
            }
            return null;
        }
        finally { NativeMemory.Free(buf); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public long Luid; public uint Attributes; }

    [DllImport("ntdll.dll")] private static extern int NtQueryTimerResolution(out uint maximumResolution, out uint minimumResolution, out uint currentResolution);
    [DllImport("ntdll.dll")] private static extern int NtSetTimerResolution(uint desiredResolution, bool setResolution, out uint currentResolution);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern unsafe bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);
}
