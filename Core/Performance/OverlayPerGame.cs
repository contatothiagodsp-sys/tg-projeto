namespace ThgOptimization.Core.Performance;

/// <summary>
/// Layout do overlay por jogo. A escolha do jogo só se sobrepõe à global, que nunca é alterada: ao fechar o jogo, não há nada para restaurar.
/// </summary>
public static class OverlayPerGame
{
    public static HashSet<string> Items(HashSet<string> global, GameProfile? profile) =>
        profile?.OverlayItems is { } items ? items.ToHashSet() : global;

    public static string Corner(string global, GameProfile? profile) =>
        string.IsNullOrEmpty(profile?.OverlayCorner) ? global : profile.OverlayCorner;

    public static bool HasOverride(GameProfile? profile) => profile?.OverlayItems != null || !string.IsNullOrEmpty(profile?.OverlayCorner);

    public static void Use(GameProfile profile, IEnumerable<string> items, string corner)
    {
        profile.OverlayItems = items.ToList();
        profile.OverlayCorner = corner;
    }

    public static void Clear(GameProfile profile)
    {
        profile.OverlayItems = null;
        profile.OverlayCorner = null;
    }
}
