using ThgOptimization.Core;

namespace ThgOptimization.UI.Services;

/// <summary>
/// Navegação para os ViewModels sem depender da janela: a MainWindow registra o próprio NavigateTo ao abrir
/// (a janela é criada depois dos ViewModels que ela usa, então a ligação é feita por aqui).
/// </summary>
public sealed class NavigationService : INavigationService
{
    private Action<string>? _handler;

    public void Attach(Action<string> handler) => _handler = handler;

    public void NavigateTo(string page) => _handler?.Invoke(page);
}
