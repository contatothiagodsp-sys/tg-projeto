using System.Text.RegularExpressions;

namespace ThgOptimization.Core.Performance;

/// <summary>
/// Opção "Microsoft Security Level 2" nas placas MSI AM5. O Windows não informa o valor dela, e a ligação com a perda de FPS não está comprovada (a MSI não
/// documenta a opção, e o teste publicado que a cita mudou também BIOS, AGESA e drivers). Por isso o item é só informativo e pede para medir.
/// </summary>
public static class MsiSecurityAdvisor
{
    private static readonly Regex Am5Chipset = new(@"\b(X870E?|X670E?|B850|B840|B650E?|A620)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool Applies(string boardMaker, string boardModel) =>
        (boardMaker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || boardMaker.Contains("MSI", StringComparison.OrdinalIgnoreCase))
        && Am5Chipset.IsMatch(boardModel);

    /// <summary>Null = outra placa: o item não aparece.</summary>
    public static GamerCheck? Build(string boardMaker, string boardModel)
    {
        if (!Applies(boardMaker, boardModel)) return null;
        return new GamerCheck
        {
            Id = "bios.msi.security", Area = "BIOS", Title = "Opção de segurança da MSI na BIOS", Weight = 0, Level = CheckLevel.Info,
            Found = $"Sua placa (MSI {boardModel.Trim()}) pode ter a opção 'Microsoft Security Level 2'. O Windows não informa se ela está ligada.",
            Advice = "A MSI não documenta o que essa opção faz. Um teste publicado viu até 15% menos FPS numa placa com ela ligada, mas a causa não foi comprovada, porque BIOS e drivers também mudaram. " +
                     "É uma opção de segurança: desligá-la troca proteção por um ganho que pode nem existir no seu PC. Meça antes de decidir.",
            Fix = FixKind.Bios, FixLabel = "GUIA DA BIOS",
            FixTarget = "1. Rode um benchmark na página BENCH e anote o resultado.\n" +
                        "2. Na BIOS, procure 'Microsoft Security Level'. O caminho varia com a versão da BIOS; se a opção não aparecer, sua placa não a tem e nada mais é preciso.\n" +
                        "3. Anote o valor atual antes de mudar, salve com F10 e rode o mesmo benchmark.\n" +
                        "4. Sem diferença clara, volte ao valor anotado.",
        };
    }
}
