using System.IO;
using System.Runtime.CompilerServices;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

internal static class TestSetup
{
    /// <summary>Os testes exercitam caminhos que registram erros: nada disso pode cair no thg.log real de quem roda os testes.</summary>
    [ModuleInitializer]
    internal static void RedirectLog() =>
        AppLog.RedirectAll(Path.Combine(Path.GetTempPath(), $"thg-testes-{Environment.ProcessId}.log"));

    /// <summary>
    /// As classes de teste rodam em paralelo, e várias seguram threads do pool de propósito (leituras de sensor presas, esperas com prazo).
    /// Com o mínimo padrão (um por núcleo: 4 no CI), o pool cria threads novas aos poucos, uma ou duas por segundo. Um Task.Run de outro
    /// teste pode ficar segundos na fila e estourar um prazo de 5 s. É a explicação compatível com as duas falhas isoladas vistas no CI
    /// (Sensor_exception_does_not_stop_the_loop e Hardware_error_rolls_back_at_once_without_waiting_for_a_reading): nas duas, o código sob
    /// teste só precisa de uma thread do pool, e nenhuma falhou fora de execução em paralelo. A falha não foi reproduzida sob demanda.
    /// Com o mínimo alto, a thread nasce na hora e os prazos medem o código, não a fila. Os testes e os prazos não mudaram.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }
}
