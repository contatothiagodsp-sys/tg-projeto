using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ThgOptimization.Infrastructure;

/// <summary>Um processo na lista do Windows. Name igual ao de Process.ProcessName; tempo de CPU (kernel + usuário) em unidades de 100 ns.</summary>
public readonly record struct ProcessSnapshot(int Pid, string Name, long TotalTime100ns);

/// <summary>
/// Consultas a processos que sempre liberam os objetos Process: cada um segura um handle do Windows até ser descartado,
/// e "GetProcessesByName(x).Length > 0" chamado a cada poucos segundos deixava handles acumulando até o próximo GC.
/// </summary>
public static class ProcessUtil
{
    // Direitos de OpenProcess usados pelas sondagens: mesmos que o Process pede depois em cada operação.
    public const uint QueryLimited = 0x1000, QueryInformation = 0x0400, SetInformation = 0x0200, SetLimitedInformation = 0x2000, AllAccess = 0x1FFFFF;

    private const int SystemProcessInformation = 5, SystemProcessIdInformation = 88;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaxSnapshotBuffer = 8 << 20;
    private static int _snapshotSizeHint = 1 << 20; // 256 KB nunca bastam: 200 processos e 4.500 threads já retornam ~490 KB

    /// <summary>
    /// Lista de processos direto do kernel: não abre nenhum processo (Vanguard-safe), não lança exceção e não cria objetos
    /// Process nem as listas de threads do GetProcesses. Vazia = o Windows recusou; quem chama deve pular a rodada.
    /// </summary>
    public static unsafe List<ProcessSnapshot> Snapshot()
    {
        var list = new List<ProcessSnapshot>(256);
        try
        {
            int size = Volatile.Read(ref _snapshotSizeHint);
            while (size <= MaxSnapshotBuffer)
            {
                // ArrayPool: sem free manual (nada de vazamento ou double-free) e sem lock entre o Tick e o overlay.
                byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    int status;
                    uint returned;
                    nint address;
                    fixed (byte* p = buffer)
                    {
                        address = (nint)p;
                        status = NtQuerySystemInformation(SystemProcessInformation, p, (uint)buffer.Length, &returned);
                    }
                    if (status == 0)
                    {
                        if (returned <= (uint)buffer.Length) Parse(buffer, (int)returned, address, list);
                        Volatile.Write(ref _snapshotSizeHint, buffer.Length);
                        return list;
                    }
                    if (status != StatusInfoLengthMismatch) return list;
                    // Cresce ao menos o dobro (o laço sempre termina), com folga para processos que abrirem até a próxima chamada.
                    size = (int)Math.Min(int.MaxValue, Math.Max(buffer.Length * 2L, returned + 64L * 1024));
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        catch { list.Clear(); }
        return list;
    }

    /// <summary>
    /// Percorre os blocos SYSTEM_PROCESS_INFORMATION sem desreferenciar nenhum ponteiro vindo do kernel: tudo é lido por
    /// Span com limite. ImageName.Buffer é um endereço absoluto, então vira um deslocamento relativo a <paramref name="baseAddress"/>
    /// e só é lido se couber nos bytes que o kernel escreveu. Bloco inválido encerra a leitura; nome inválido descarta a entrada.
    /// </summary>
    internal static void Parse(byte[] buffer, int length, nint baseAddress, List<ProcessSnapshot> into)
    {
        var data = new ReadOnlySpan<byte>(buffer, 0, Math.Clamp(length, 0, buffer.Length));
        int header = Unsafe.SizeOf<SYSTEM_PROCESS_INFORMATION>();
        long offset = 0;
        while (offset + header <= data.Length)
        {
            var entry = MemoryMarshal.Read<SYSTEM_PROCESS_INFORMATION>(data.Slice((int)offset));
            int pid = (int)entry.UniqueProcessId;
            if (ReadName(data, baseAddress, entry.ImageName, pid) is { } name)
                into.Add(new ProcessSnapshot(pid, name, entry.KernelTime + entry.UserTime));
            if (entry.NextEntryOffset < header) return; // 0 = último bloco; menor que o cabeçalho = corrompido (nunca voltar nem parar no lugar)
            offset += entry.NextEntryOffset;
        }
    }

    private static string? ReadName(ReadOnlySpan<byte> data, nint baseAddress, UNICODE_STRING image, int pid)
    {
        if (image.Buffer == 0 || image.Length == 0)
            return pid switch { 0 => "Idle", 4 => "System", _ => pid.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        long relative = (long)image.Buffer - (long)baseAddress;
        if (relative < 0 || image.Length % 2 != 0 || relative + image.Length > data.Length) return null;
        return ShortName(MemoryMarshal.Cast<byte, char>(data.Slice((int)relative, image.Length)));
    }

    /// <summary>Como Process.ProcessName: sem a pasta e só ".exe" sai. GetFileNameWithoutExtension cortaria também "foo.scr".</summary>
    private static string ShortName(ReadOnlySpan<char> name)
    {
        name = name[(name.LastIndexOf('\\') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return new string(name);
    }

    /// <summary>
    /// Nome de um processo pelo PID, em O(1) e sem abrir o processo: o PID pode ser o Valorant, que o tHg nunca abre.
    /// SystemProcessIdInformation não é documentada; se o Windows recusar, cai na lista completa. null = o processo não existe.
    /// Igual a Process.ProcessName para programas; processos do kernel sem .exe podem diferir ("MemCompression" em vez de
    /// "Memory Compression"). Serve para mostrar o nome de um jogo; comparar com listas de processos é com o Snapshot.
    /// </summary>
    public static unsafe string? NameOf(int pid)
    {
        try
        {
            Span<char> chars = stackalloc char[1024];
            fixed (char* p = chars)
            {
                var info = new SYSTEM_PROCESS_ID_INFORMATION
                {
                    ProcessId = pid,
                    ImageName = new UNICODE_STRING { Length = 0, MaximumLength = (ushort)(chars.Length * sizeof(char)), Buffer = (nint)p },
                };
                uint returned;
                int status = NtQuerySystemInformation(SystemProcessIdInformation, &info, (uint)sizeof(SYSTEM_PROCESS_ID_INFORMATION), &returned);
                // O tamanho vem do kernel: o Span recusa o que passar do buffer, em vez de ler memória alheia.
                if (status == 0 && info.ImageName.Length is > 0 and <= 2048 && info.ImageName.Length % 2 == 0)
                    return ShortName(chars[..(info.ImageName.Length / sizeof(char))]);
            }
        }
        catch { }
        foreach (var s in Snapshot())
            if (s.Pid == pid) return s.Name;
        return null;
    }

    /// <summary>
    /// O processo saiu da lista? O nome também conta, porque o PID pode ter sido reaproveitado. Lista vazia significa que
    /// o Windows não respondeu nesta rodada, não que o jogo fechou: encerrar a sessão por isso seria pior do que esperar a próxima.
    /// </summary>
    public static bool HasExited(List<ProcessSnapshot> snapshot, int pid, string name)
    {
        if (snapshot.Count == 0) return false;
        foreach (var s in snapshot)
            if (s.Pid == pid) return !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>O Windows daria estes direitos sobre o processo? Falha sem exceção, ao contrário das propriedades de Process.</summary>
    public static bool CanOpen(int pid, uint access)
    {
        using SafeProcessHandle h = OpenProcessSafe(access, false, pid);
        return !h.IsInvalid;
    }

    /// <summary>
    /// Executa com um handle só com estes direitos, fechado logo em seguida. É o caminho para mexer no processo do jogo:
    /// Process.Handle abre com PROCESS_ALL_ACCESS (inclui ler e escrever na memória) e o mantém aberto até o Dispose, a sessão inteira.
    /// false = o Windows negou, sem exceção.
    /// </summary>
    public static bool WithHandle(int pid, uint access, Func<IntPtr, bool> action)
    {
        using SafeProcessHandle h = OpenProcessSafe(access, false, pid);
        return !h.IsInvalid && action(h.DangerousGetHandle());
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UNICODE_STRING{ public ushort Length, MaximumLength; public nint Buffer; }

    /// <summary>
    /// Truncada de propósito: só até o último campo lido; o resto (e as threads) é pulado por NextEntryOffset.
    /// nint e o alinhamento natural geram os dois layouts. Offsets conferidos contra o Windows real e travados em teste.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_PROCESS_INFORMATION
    {                                               //  x64  x86
        public uint NextEntryOffset;                //    0    0
        public uint NumberOfThreads;                //    4    4
        public long WorkingSetPrivateSize;          //    8    8
        public uint HardFaultCount;                 //   16   16
        public uint NumberOfThreadsHighWatermark;   //   20   20
        public ulong CycleTime;                     //   24   24
        public long CreateTime;                     //   32   32
        public long UserTime;                       //   40   40
        public long KernelTime;                     //   48   48
        public UNICODE_STRING ImageName;            //   56   56  (Buffer em 64 / 60)
        public int BasePriority;                    //   72   64
        public nint UniqueProcessId;                //   80   68
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_ID_INFORMATION { public nint ProcessId; public UNICODE_STRING ImageName; }

    [DllImport("ntdll.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe int NtQuerySystemInformation(int infoClass, void* info, uint length, uint* returned);

    /// <summary>
    /// Caminho do executável só com PROCESS_QUERY_LIMITED_INFORMATION. Process.MainModule abre com VM_READ e lê a memória
    /// do processo, o que anti-cheats (EAC, BattlEye) registram quando o alvo é o jogo. null = o Windows negou.
    /// </summary>
    public static string? ImagePath(Process p) => ImagePath(p.Id);

    /// <inheritdoc cref="ImagePath(Process)"/>
    public static string? ImagePath(int pid)
    {
        try
        {
            using SafeProcessHandle h = OpenProcessSafe(QueryLimited, false, pid);
            if (h.IsInvalid) return null;
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcessSafe(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle h, int flags, System.Text.StringBuilder name, ref int size);

    /// <summary>Há algum processo com este nome (sem ".exe")?</summary>
    public static bool IsRunning(string name)
    {
        var procs = Process.GetProcessesByName(name);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>Executa a ação em cada processo com este nome e descarta todos depois.</summary>
    public static void ForEach(string name, Action<Process> action)
    {
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
                try { action(p); } catch { }
    }
}
