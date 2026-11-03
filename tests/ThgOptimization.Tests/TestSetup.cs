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
}
