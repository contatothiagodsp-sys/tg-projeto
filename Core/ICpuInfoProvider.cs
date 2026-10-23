namespace ThgOptimization.Core;

/// <summary>Uma linha "rótulo: valor" das fichas e painéis ao vivo.</summary>
public sealed record InfoRow(string Key, string Value);

/// <summary>Um núcleo na aba Processador: clock, temperatura e carga de cada thread, tudo lido dos sensores.</summary>
public sealed record CpuCoreRow(string Core, string Clock, string Temp, string Load);

/// <summary>Ficha estática do processador.</summary>
public sealed record CpuStaticInfo(string Name, string Vendor, IReadOnlyList<InfoRow> Rows);

/// <summary>Plano de energia ativo e core parking (configuração do plano e estado atual).</summary>
public sealed record CpuPowerInfo(string Plan, string ParkingSetting, (int Parked, int Total)? ParkedNow);

/// <summary>
/// Leitura do processador. Tudo vem do hardware ou do Windows; o que não tiver leitura volta como "—".
/// Os métodos que tocam WMI/powercfg são lentos e devem ser chamados fora da thread da janela.
/// </summary>
public interface ICpuInfoProvider
{
    CpuStaticInfo ReadStatic();
    CpuPowerInfo ReadPower();
    /// <summary>Resumo ao vivo (rápido: só lê os sensores já atualizados pelo SensorMonitor).</summary>
    IReadOnlyList<InfoRow> ReadLive();
    IReadOnlyList<CpuCoreRow> ReadCores();
    /// <summary>Motivo de faltar temperatura/consumo da CPU, ou null se os sensores estão lendo.</summary>
    string? SensorProblem();
}
