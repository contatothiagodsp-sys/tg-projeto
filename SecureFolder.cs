using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using ThgOptimization.Infrastructure;

namespace ThgOptimization;

/// <summary>
/// Pastas do tHg que só administradores (e o SYSTEM) podem alterar, em %ProgramData%\tHgOptimization.
///
/// Por quê: o tHg roda como administrador. Tudo que ele executa ou carrega (instaladores baixados, drivers, a DLL do iCUE)
/// e tudo que ele lê para decidir o que executar (settings.json: programas auxiliares, instalador pendente, valores a
/// restaurar) não pode ficar numa pasta que um programa comum, sem administrador, consiga trocar. Antes isso ficava em
/// %AppData% e %TEMP%, onde qualquer processo do usuário escreve: um malware sem privilégio poderia trocar um instalador
/// entre a verificação e a execução, ou incluir um "programa auxiliar" e ganhar administrador.
/// </summary>
public static class SecureFolder
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "tHgOptimization");

    internal static readonly object Gate = new();
    private static readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);

    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;
    // Quem não é SYSTEM nem administrador não pode ter nenhum destes direitos: cada um permite trocar o conteúdo ou as próprias regras.
    private const int WriteLikeRights = (int)(FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
                                              | FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
                                              | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)
                                        | GenericAll | GenericWrite;

    /// <summary>
    /// Pasta protegida (criada e travada na primeira chamada). sub pode ter vários níveis ("users\\S-1-5-…").
    /// Sem proteção comprovada, lança UnauthorizedAccessException: o tHg roda como administrador e nunca usa uma pasta que um programa comum possa alterar.
    /// </summary>
    public static string Get(string sub) => Secure(Root, sub, EnsureTrusted, VerifyTrustedDirectory);

    /// <summary>Pasta do usuário atual (configurações, logs, backups).</summary>
    public static string User => Get(Path.Combine("users", WindowsIdentity.GetCurrent().User?.Value ?? "default"));

    /// <summary>Protege e confere cada nível, da raiz até a pasta pedida. Só entra no cache o que passou pela conferência.</summary>
    internal static string Secure(string root, string sub, Action<string> ensure, Action<string> verify)
    {
        string dir = Path.Combine(root, sub);
        Exception failure;
        lock (Gate)
        {
            if (Done.Contains(dir) && Directory.Exists(dir)) return dir;
            try
            {
                string level = root;
                ensure(level);
                verify(level);
                foreach (string part in sub.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    level = Path.Combine(level, part);
                    ensure(level);
                    verify(level);
                }
                Done.Add(dir);
                return dir;
            }
            catch (Exception ex) { failure = ex; }
        }
        // Fora da trava: o registro fica nesta mesma pasta, então AppLog segura a trava dele e pede esta. Registrar aqui dentro seria um deadlock.
        AppLog.Error("secure-folder", $"{dir}: {failure.GetType().Name}: {failure.Message}");
        throw new UnauthorizedAccessException($"Não foi possível garantir a proteção da pasta {dir}: {failure.Message}", failure);
    }

    internal static void EnsureTrusted(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (info.Exists && (!IsTrustedOwner(info) || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            // Criada por outro usuário (ou é um atalho de pasta): tira do caminho e cria uma nova, nossa.
            string quarantine = dir + ".nao-confiavel-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            Directory.Move(dir, quarantine);
            info = new DirectoryInfo(dir);
        }

        var sec = TrustedRules();
        sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        // Criada já com as regras: em %ProgramData% um usuário comum pode escrever em pastas novas até o ACL ser trocado.
        if (!info.Exists) { info.Create(sec); return; }
        try { info.SetAccessControl(sec); }
        // O Windows recusou a dona: aplica só as regras. Se a dona atual não servir, VerifyTrustedDirectory recusa a pasta.
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException) { info.SetAccessControl(TrustedRules()); }
    }

    private static DirectorySecurity TrustedRules()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Usuários comuns só leem (abrir o log, por exemplo); não escrevem.
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>Relê do disco o que ficou gravado: o que vale é o estado real da pasta, não o que foi pedido ao Windows.</summary>
    internal static void VerifyTrustedDirectory(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists) throw new DirectoryNotFoundException($"A pasta {dir} não existe.");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException($"A pasta {dir} é um atalho.");
        if (FindAclProblem(info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)) is string problem)
            throw new UnauthorizedAccessException($"{dir}: {problem}");
    }

    /// <summary>O que impede estas regras de proteger a pasta; null se estão certas.</summary>
    internal static string? FindAclProblem(DirectorySecurity sec)
    {
        // A dona sempre pode reescrever as regras. Uma conta de usuário não serve: sob o UAC, os programas dela sem elevação têm essa conta no token.
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !IsPrivileged(owner)) return $"a dona não é confiável ({owner?.Value ?? "desconhecida"})";
        if (!sec.AreAccessRulesProtected) return "as regras ainda herdam da pasta acima";

        bool system = false, admins = false;
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            int mask = (int)rule.FileSystemRights;
            if (!IsPrivileged(sid))
            {
                if ((mask & WriteLikeRights) != 0) return $"{sid.Value} pode alterar a pasta";
                continue;
            }
            bool full = (mask & GenericAll) != 0 || (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            bool appliesHereAndBelow = !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                                       && rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            if (full && appliesHereAndBelow)
            {
                system |= sid.IsWellKnown(WellKnownSidType.LocalSystemSid);
                admins |= sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
            }
        }
        return system && admins ? null : "faltam as regras de SYSTEM e dos administradores";
    }

    private static bool IsPrivileged(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private static bool IsTrustedOwner(DirectoryInfo d)
    {
        try
        {
            var owner = d.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                                     || owner.IsWellKnown(WellKnownSidType.AccountAdministratorSid)
                                     || IsAdminAccount(owner));
        }
        catch { return false; }
    }

    /// <summary>Pasta criada antes desta versão pelo próprio tHg (já elevado): a dona é a conta do usuário administrador atual.</summary>
    private static bool IsAdminAccount(SecurityIdentifier sid)
    {
        try { return sid == WindowsIdentity.GetCurrent().User && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }
}
