using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

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

    private static readonly object Gate = new();
    private static readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pasta protegida (criada e travada na primeira chamada). sub pode ter vários níveis ("users\\S-1-5-…").</summary>
    public static string Get(string sub)
    {
        string root = Root, dir = Path.Combine(root, sub);
        lock (Gate)
        {
            if (Done.Contains(dir) && Directory.Exists(dir)) return dir;
            try
            {
                EnsureTrusted(root);
                Directory.CreateDirectory(dir);
                // Subpastas herdam a regra da raiz; se alguém criou uma antes com outra dona, refaz.
                EnsureTrusted(dir);
                Done.Add(dir);
            }
            catch { Directory.CreateDirectory(dir); } // sem permissão para travar (não deveria acontecer como admin): segue
        }
        return dir;
    }

    /// <summary>Pasta do usuário atual (configurações, logs, backups).</summary>
    public static string User => Get(Path.Combine("users", WindowsIdentity.GetCurrent().User?.Value ?? "default"));

    private static void EnsureTrusted(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (info.Exists && (!IsTrustedOwner(info) || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            // Criada por outro usuário (ou é um atalho de pasta): tira do caminho e cria uma nova, nossa.
            string quarantine = dir + ".nao-confiavel-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            Directory.Move(dir, quarantine);
            info = new DirectoryInfo(dir);
        }
        if (!info.Exists) info.Create();

        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Usuários comuns só leem (abrir o log, por exemplo); não escrevem.
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        try { sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)); } catch { }
        try { info.SetAccessControl(sec); }
        catch { sec = new DirectorySecurity(); CopyRules(sec); info.SetAccessControl(sec); } // sem trocar a dona
    }

    private static void CopyRules(DirectorySecurity sec)
    {
        sec.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
    }

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
