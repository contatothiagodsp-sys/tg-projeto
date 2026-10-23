namespace ThgOptimization.Core;

/// <summary>Abre uma página do app pelo nome do destino (ex.: "PageBench"), como clicar na aba dela.</summary>
public interface INavigationService
{
    void NavigateTo(string page);
}
