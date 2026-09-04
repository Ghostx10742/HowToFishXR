namespace HowToFishVR.Localization;

/// <summary>
/// Localization shim. The optional How to Fish LocalizationAPI is no longer referenced (removing it
/// must never break loading), so this simply returns the English fallback baked into each call site.
/// If translated menus are wanted later, this can be re-wired to an API via reflection (no hard type
/// dependency) so deleting the API can never cause a TypeLoadException again.
/// </summary>
public static class Loc
{
    public static void Register() { /* English only */ }

    public static string T(string key, string fallback) => fallback;
}
