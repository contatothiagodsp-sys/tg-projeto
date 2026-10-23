using System.Runtime;
using System.Runtime.InteropServices;

namespace ThgOptimization;

/// <summary>
/// Bloco de memória pedido direto ao Windows (VirtualAlloc), fora do coletor de lixo do .NET. Dispose devolve na hora
/// (VirtualFree/MEM_RELEASE): ao parar um teste de RAM a memória do processo cai no mesmo instante, em vez de ficar
/// retida no heap de objetos grandes do .NET até a próxima coleta.
/// </summary>
public sealed unsafe class NativeBuffer : IDisposable
{
    private void* _ptr;
    public long Bytes { get; }
    public long* Longs => (long*)_ptr;
    public long Count => Bytes / sizeof(long);

    private NativeBuffer(void* p, long bytes) { _ptr = p; Bytes = bytes; }

    /// <summary>null se o Windows não tiver memória para o bloco.</summary>
    public static NativeBuffer? TryAlloc(long bytes)
    {
        void* p = VirtualAlloc(null, (nuint)bytes, 0x1000 | 0x2000 /* COMMIT | RESERVE */, 0x04 /* READWRITE */);
        return p == null ? null : new NativeBuffer(p, bytes);
    }

    public void Dispose()
    {
        if (_ptr != null) { VirtualFree(_ptr, 0, 0x8000 /* RELEASE */); _ptr = null; }
        GC.SuppressFinalize(this);
    }

    ~NativeBuffer() { if (_ptr != null) VirtualFree(_ptr, 0, 0x8000); }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern void* VirtualAlloc(void* addr, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(void* addr, nuint size, uint type);
}

/// <summary>Devolve ao Windows a memória que o próprio tHg não usa mais (depois de testes pesados).</summary>
public static class MemoryRelease
{
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch { }
    }

    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
