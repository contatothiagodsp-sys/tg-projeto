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

/// <summary>
/// Compacta o heap do .NET depois de testes pesados. O working set fica com o Windows: forçar as páginas para fora faz o próximo
/// timer da janela puxá-las de volta do disco, e esses page faults aparecem como engasgo no jogo.
/// O modo Aggressive é outra coisa: devolve ao Windows só o que o coletor tem livre, e nada do que está em uso sai da RAM. Sem ele, o coletor segura uma
/// geração nova do tamanho do cache L3: num 9800X3D, com 96 MB de L3, eram 49 MB presos com o heap em 23 MB. Medido com a janela escondida: 44 MB a menos.
/// </summary>
public static class MemoryRelease
{
    public static void Trim()
    {
        try { Collect(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Infrastructure.AppLog.Warn("memória", ex); }
    }

    /// <summary>Uma passada basta: o modo Aggressive já compacta, inclusive os objetos grandes.</summary>
    internal static void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
