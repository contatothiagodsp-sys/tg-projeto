using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Converte exceções em frases para o usuário final, com o que fazer a seguir. Mensagens que o próprio tHg escreveu
/// (já em português e pensadas para o usuário) passam intactas; o texto técnico do .NET (em inglês) não aparece mais,
/// exceto como "detalhe" no caso desconhecido, para o suporte conseguir entender o relato.
/// </summary>
public static class FriendlyError
{
    private const int AccessDenied = 5, Cancelled = 1223;
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);
    private const int DiskFull = unchecked((int)0x80070070), HandleDiskFull = unchecked((int)0x80070027);
    private const int DeviceRemoved = unchecked((int)0x887A0005), DeviceHung = unchecked((int)0x887A0006), DeviceReset = unchecked((int)0x887A0007);

    public static string Of(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } agg) return Of(agg.InnerExceptions[0]);
        if (IsWrapper(ex) && ex.InnerException != null) return Of(ex.InnerException);

        // Mensagem escrita pelo tHg (lançada do nosso próprio código): já é amigável.
        if (ex.Source == typeof(FriendlyError).Assembly.GetName().Name && ex.GetType() is var t
            && (t == typeof(InvalidOperationException) || t == typeof(Exception) || t == typeof(InvalidDataException) || t == typeof(IOException)))
            return ex.Message;

        switch (ex)
        {
            case UnauthorizedAccessException:
            case Win32Exception { NativeErrorCode: AccessDenied }:
                return "O Windows negou o acesso. Confirme que o tHg está aberto como administrador; se continuar, o antivírus pode estar bloqueando — libere o tHg nele.";
            case Win32Exception { NativeErrorCode: Cancelled }:
                return "A permissão foi recusada na janela de confirmação do Windows.";
            case Win32Exception { NativeErrorCode: 2 or 3 }:
                return "O programa necessário não foi encontrado. Ele pode ter sido desinstalado ou movido.";
            case FileNotFoundException f:
                string name = Path.GetFileName(f.FileName) is { Length: > 0 } n ? $"\"{n}\" " : "";
                return $"O arquivo {name}não foi encontrado. Pode ter sido movido, apagado ou bloqueado pelo antivírus.";
            case DirectoryNotFoundException:
                return "A pasta não foi encontrada. Pode ter sido movida ou apagada.";
            case IOException io when io.HResult is SharingViolation or LockViolation:
                return "O arquivo está aberto em outro programa. Feche o programa que está usando o arquivo e tente de novo.";
            case IOException io when io.HResult is DiskFull or HandleDiskFull:
                return "O disco está cheio. Libere espaço (Otimizar → Limpeza) e tente de novo.";
            case OperationCanceledException when ex is TaskCanceledException { InnerException: TimeoutException }:
            case TimeoutException:
                return "Demorou demais para responder. Confira a conexão e tente de novo em alguns instantes.";
            case HttpRequestException { StatusCode: { } code }:
                return (int)code switch
                {
                    403 or 429 => "O site recusou a consulta agora (limite de acessos). Tente de novo em alguns minutos.",
                    404 => "O arquivo não está mais disponível no site do fabricante. Tente de novo mais tarde ou baixe pela página oficial.",
                    >= 500 => "O site do fabricante está fora do ar no momento. Tente de novo mais tarde.",
                    _ => $"O site respondeu com erro ({(int)code}). Tente de novo mais tarde.",
                };
            case HttpRequestException:
            case SocketException:
                return "Sem conexão com a internet, ou o site não respondeu. Confira a conexão e tente de novo.";
            case OperationCanceledException:
                return "Cancelado.";
            case System.Management.ManagementException:
                return "O Windows não respondeu à consulta de hardware (WMI). Reiniciar o PC costuma resolver.";
            case COMException com when com.HResult is DeviceRemoved or DeviceHung or DeviceReset:
                return "A placa de vídeo parou de responder (o driver foi reiniciado). Volte o overclock ao padrão ou atualize o driver de vídeo.";
            case OutOfMemoryException:
                return "Faltou memória para concluir. Feche programas pesados e tente de novo.";
            case InvalidDataException:
                return "O arquivo está corrompido ou num formato inesperado. Baixe ou gere o arquivo de novo.";
        }
        string detail = ex.Message.Length > 160 ? ex.Message[..160] + "…" : ex.Message;
        return $"Algo deu errado. Tente de novo; se continuar, use Configurações → Relatar um problema. (Detalhe: {detail})";
    }

    // Exceção de reflexão/invocação que só embrulha a verdadeira.
    private static bool IsWrapper(Exception ex) => ex is System.Reflection.TargetInvocationException or TypeInitializationException;
}
