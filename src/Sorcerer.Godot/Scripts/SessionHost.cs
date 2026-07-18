using Sorcerer.Core;
using Sorcerer.Core.Characters;

namespace Sorcerer.Godot;

/// <summary>
/// Hands the live GameSession across a Godot scene swap. A plain static field is enough since
/// GameSession is an ordinary C# object (not a Godot node/resource) and survives
/// ChangeSceneToFile for free.
/// </summary>
public static class SessionHost
{
    public static GameSession? Session;

    /// <summary>A character chosen on the creation screen, waiting for Main to build the run
    /// around it. Main must null this out when consuming it, or a later scene swap back into
    /// Main would silently restart the run.</summary>
    public static CharacterBuild? PendingBuild;

    /// <summary>Esc-menu provider fields, carried across the Main → CharacterCreation → Main
    /// round trip (Main is rebuilt on each swap, so its LineEdits alone can't hold them).</summary>
    public static string? ProviderOverride;
    public static string? HostOverride;
    public static string? ModelOverride;
    public static string? EffortOverride;

    /// <summary>Prevents the same missing-Gemini-key dialog from appearing after every scene swap.</summary>
    public static bool GeminiSetupNoticeShown;
}
