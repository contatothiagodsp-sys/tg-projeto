using Microsoft.Win32;

namespace ThgOptimization.Core.Performance;

/// <summary>Um valor DWORD do registro, separado para os testes não tocarem o Windows.</summary>
public interface IRegistryDword
{
    int? Read();
    void Write(int value);
    void Delete();
}

public sealed class RegistryDword : IRegistryDword
{
    private readonly string _path, _name;

    public RegistryDword(string path, string name)
    {
        _path = path;
        _name = name;
    }

    public int? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(_path);
        return key?.GetValue(_name) is int value ? value : null;
    }

    public void Write(int value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(_path);
        key.SetValue(_name, value, RegistryValueKind.DWord);
    }

    public void Delete()
    {
        using var key = Registry.LocalMachine.OpenSubKey(_path, writable: true);
        key?.DeleteValue(_name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Prioridade do agendador do Windows e da GPU para jogos: ajustes clássicos, com ganho que depende do jogo. O TURBO só mostra o valor e o guia para medir. Aplicar exige
/// confirmação explícita, nunca entra em "corrigir tudo", e o valor anterior fica guardado.
/// </summary>
public static class SchedulerTweak
{
    public const string W32Backup = "sec.sched.w32", GpuBackup = "sec.sched.gpu";
    public const int W32Target = 0x2A, GpuTarget = 8;
    private const string Absent = "<ausente>";

    public static readonly IRegistryDword W32Value = new RegistryDword(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation");
    public static readonly IRegistryDword GpuValue = new RegistryDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority");

    private static IRegistryDword CellOf(string backupKey) => backupKey == W32Backup ? W32Value : GpuValue;

    /// <summary>Lê os três campos de bits: tamanho do intervalo, variável ou fixo, e quanto o programa em primeiro plano ganha.</summary>
    internal static string DescribeW32(int value)
    {
        int length = value >> 4 & 3, kind = value >> 2 & 3, boost = value & 3;
        string interval = length == 2 ? "intervalos curtos" : length == 1 ? "intervalos longos" : "intervalos padrão do Windows";
        string fixedOrVariable = kind == 2 ? "fixos" : kind == 1 ? "variáveis" : "padrão";
        string foreground = boost switch { 2 => "primeiro plano 3× maior", 1 => "primeiro plano 2× maior", _ => "sem vantagem para o primeiro plano" };
        return $"{interval}, {fixedOrVariable}, {foreground}";
    }

    public static GamerCheck BuildW32Check(int? current, bool hasBackup)
    {
        string now = current is int v ? $"0x{v:X2} ({DescribeW32(v)})" : "sem valor (padrão do Windows)";
        return new GamerCheck
        {
            Id = "comp.scheduler.measure", Area = "WINDOWS", Title = "Prioridade do agendador (W32PrioritySeparation) — medir", Weight = 0, Level = CheckLevel.Info,
            Found = "Atual: " + now,
            Advice = "Define quanto o Windows favorece o programa em primeiro plano. 0x2A dá ao jogo intervalos curtos e fixos, e o resto do sistema responde um pouco menos. O ganho depende do jogo: pode não existir.",
            Fix = FixKind.Bios, FixLabel = "COMO MEDIR",
            FixTarget = "1. Anote o valor atual e rode um benchmark na página BENCH.\n2. Mude para 0x2A e reinicie o PC.\n3. Rode o mesmo benchmark e compare.\n4. Se não ganhar, volte ao valor anotado.",
            SecondaryLabel = hasBackup ? "VOLTAR AO ANTERIOR" : current == W32Target ? "" : "APLICAR 0x2A",
        };
    }

    public static GamerCheck BuildGpuCheck(int? current, bool hasBackup) => new()
    {
        Id = "comp.scheduler.gpu", Area = "WINDOWS", Title = "Prioridade de GPU para jogos (MMCSS) — medir", Weight = 0, Level = CheckLevel.Info,
        Found = "Atual: " + (current is int v ? v.ToString() : "sem valor (padrão do Windows)"),
        Advice = "É a prioridade que o Windows pede para a GPU nas tarefas de jogo. O padrão do Windows é 8. O efeito depende do jogo e do driver: meça antes de mudar.",
        Fix = FixKind.Bios, FixLabel = "COMO MEDIR",
        FixTarget = "1. Anote o valor atual e rode um benchmark na página BENCH.\n2. Mude GPU Priority para 8 em Tasks\\Games e reinicie o PC.\n3. Rode o mesmo benchmark e compare.\n4. Se não ganhar, volte ao valor anotado.",
        SecondaryLabel = hasBackup ? "VOLTAR AO ANTERIOR" : current == GpuTarget ? "" : "APLICAR 8",
    };

    /// <summary>Sem a confirmação do usuário, não escreve nada. Guarda o valor de antes só na primeira vez: aplicar de novo não apaga o original.</summary>
    public static string Apply(string backupKey, int value, bool confirmed, IRegistryDword cell, AppSettings s)
    {
        if (!confirmed) return "É preciso confirmar antes de aplicar.";
        if (!s.TweakBackup.ContainsKey(backupKey)) s.TweakBackup[backupKey] = cell.Read() is int before ? $"d:{before}" : Absent;
        cell.Write(value);
        return "";
    }

    /// <summary>Devolve o valor guardado e tira a cópia. O arquivo vem de settings.json: só aceita o formato que o próprio Apply grava.</summary>
    public static bool Restore(string backupKey, IRegistryDword cell, AppSettings s)
    {
        if (s.TweakBackup.GetValueOrDefault(backupKey) is not { } previous) return false;
        if (previous == Absent) cell.Delete();
        else if (previous.StartsWith("d:", StringComparison.Ordinal) && int.TryParse(previous[2..], out int value)) cell.Write(value);
        else return false;
        s.TweakBackup.Remove(backupKey);
        return true;
    }

    /// <summary>Para o "desfazer" do SHIELD, que percorre as cópias "sec." pela chave.</summary>
    public static bool RestoreByKey(string backupKey, AppSettings s) =>
        backupKey is W32Backup or GpuBackup && Restore(backupKey, CellOf(backupKey), s);
}
