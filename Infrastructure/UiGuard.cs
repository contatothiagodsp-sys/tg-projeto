using System.Windows;

namespace ThgOptimization.Infrastructure;

/// <summary>Exceção em async void sobe ao Dispatcher e derruba o app: o que roda solto na janela passa por aqui.</summary>
internal static class UiGuard
{
    /// <summary>Roda o corpo. Falha vira registro e aviso na tela, nunca exceção para quem chamou.</summary>
    public static async Task RunAsync(string area, Func<Task> body, Action<Exception> report)
    {
        try { await body(); }
        catch (Exception ex)
        {
            AppLog.Error(area, ex);
            report(ex);
        }
    }

    /// <summary>
    /// Handler de botão: desliga o botão durante o corpo e sempre o religa, inclusive depois de falha. A falha segue o caminho
    /// do RunAsync (registro e aviso na tela). Botão null: só o corpo roda, para handlers chamados fora de um clique.
    /// </summary>
    public static async Task RunButtonAsync(UIElement? button, string area, Func<Task> body, Action<Exception> report)
    {
        if (button != null) button.IsEnabled = false;
        try { await RunAsync(area, body, report); }
        finally { if (button != null) button.IsEnabled = true; }
    }

    /// <summary>
    /// Abre um recurso fora da janela. Se o app começou a fechar durante a abertura, ninguém mais vai soltá-lo: é fechado aqui e volta null,
    /// para não prender um handle de hardware até o fim do processo. Falha ao abrir também volta null, com registro.
    /// </summary>
    public static async Task<T?> OpenUnlessExitingAsync<T>(string area, Func<T?> open, Func<bool> exiting) where T : class, IDisposable
    {
        T? opened;
        try { opened = await Task.Run(open); }
        catch (Exception ex)
        {
            AppLog.Error(area, ex);
            return null;
        }
        if (opened == null || !exiting()) return opened;
        try { opened.Dispose(); }
        catch (Exception ex) { AppLog.Error(area, ex); }
        return null;
    }
}
