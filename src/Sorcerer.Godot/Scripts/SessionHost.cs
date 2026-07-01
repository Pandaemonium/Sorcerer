using Sorcerer.Core;

namespace Sorcerer.Godot;

/// <summary>
/// Hands the live GameSession across a Godot scene swap. A plain static field is enough since
/// GameSession is an ordinary C# object (not a Godot node/resource) and survives
/// ChangeSceneToFile for free.
/// </summary>
public static class SessionHost
{
    public static GameSession? Session;
}
