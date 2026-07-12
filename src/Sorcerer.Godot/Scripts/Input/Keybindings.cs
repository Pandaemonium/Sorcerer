using Godot;

// Deliberately not "Sorcerer.Godot.Input": a child namespace named Input would shadow the
// Godot.Input class for every file under Sorcerer.Godot (the minigames poll it).
namespace Sorcerer.Godot;

public enum BindableAction
{
    MoveNorth,
    MoveSouth,
    MoveWest,
    MoveEast,
    MoveNorthWest,
    MoveNorthEast,
    MoveSouthWest,
    MoveSouthEast,
    Wait,
    Inspect,
    Pickup,
    FocusSpell,
    FocusTalk,
    ToggleAutoplay,
    ToggleLlmDebug,
    OpenControls,
}

public sealed record ActionSpec(
    BindableAction Action,
    string Id,
    string Label,
    string Category,
    Key DefaultKey);

/// <summary>
/// The rebindable key table: single source of truth for which key triggers which action on
/// the play screen (Main routes both bare keys and the Ctrl+key bypass through it). User
/// overrides persist to user:// so they survive game updates and read-only install dirs.
/// Arrows and the numpad are fixed secondary movement bindings on purpose — rebinding H can
/// never strand a player with no way to move.
/// </summary>
public static class Keybindings
{
    public const string SavePath = "user://keybindings.cfg";
    private const string BindingsSection = "bindings";

    public static readonly IReadOnlyList<ActionSpec> Specs = new ActionSpec[]
    {
        new(BindableAction.MoveNorth, "move_north", "Move north", "Movement", Key.K),
        new(BindableAction.MoveSouth, "move_south", "Move south", "Movement", Key.J),
        new(BindableAction.MoveWest, "move_west", "Move west", "Movement", Key.H),
        new(BindableAction.MoveEast, "move_east", "Move east", "Movement", Key.L),
        new(BindableAction.MoveNorthWest, "move_northwest", "Move northwest", "Movement", Key.Y),
        new(BindableAction.MoveNorthEast, "move_northeast", "Move northeast", "Movement", Key.U),
        new(BindableAction.MoveSouthWest, "move_southwest", "Move southwest", "Movement", Key.B),
        new(BindableAction.MoveSouthEast, "move_southeast", "Move southeast", "Movement", Key.N),
        new(BindableAction.Wait, "wait", "Wait a turn", "Actions", Key.Period),
        new(BindableAction.Inspect, "inspect", "Inspect surroundings", "Actions", Key.I),
        new(BindableAction.Pickup, "pickup", "Pick up", "Actions", Key.G),
        new(BindableAction.FocusSpell, "focus_spell", "Jump to the spell box", "Interface", Key.C),
        new(BindableAction.FocusTalk, "focus_talk", "Start talking (fills 'talk ')", "Interface", Key.T),
        new(BindableAction.ToggleAutoplay, "toggle_autoplay", "Toggle autoplay", "Interface", Key.P),
        new(BindableAction.ToggleLlmDebug, "toggle_llm_debug", "Toggle the LLM debug view", "Interface", Key.F6),
        new(BindableAction.OpenControls, "open_controls", "Open this controls screen", "Interface", Key.F1),
    };

    // Fixed secondary movement bindings; never persisted, never rebindable, never conflicting
    // (arrow keys are in the reserved set).
    private static readonly IReadOnlyDictionary<Key, BindableAction> ArrowFallback =
        new Dictionary<Key, BindableAction>
        {
            [Key.Up] = BindableAction.MoveNorth,
            [Key.Down] = BindableAction.MoveSouth,
            [Key.Left] = BindableAction.MoveWest,
            [Key.Right] = BindableAction.MoveEast,
        };

    private static readonly HashSet<Key> ReservedKeys = new()
    {
        Key.Escape, Key.Enter, Key.KpEnter, Key.Tab, Key.Space,
        Key.Up, Key.Down, Key.Left, Key.Right,
        Key.Kp0, Key.Kp1, Key.Kp2, Key.Kp3, Key.Kp4,
        Key.Kp5, Key.Kp6, Key.Kp7, Key.Kp8, Key.Kp9,
        Key.Shift, Key.Ctrl, Key.Alt, Key.Meta, Key.None,
    };

    private static readonly Dictionary<BindableAction, Key> Current = new();

    static Keybindings()
    {
        foreach (var spec in Specs)
        {
            Current[spec.Action] = spec.DefaultKey;
        }

        Load();
    }

    public static Key KeyFor(BindableAction action) => Current[action];

    public static BindableAction? ActionForKey(Key key)
    {
        foreach (var entry in Current)
        {
            if (entry.Value == key)
            {
                return entry.Key;
            }
        }

        return ArrowFallback.TryGetValue(key, out var fallback) ? fallback : null;
    }

    public static bool IsReserved(Key key) => ReservedKeys.Contains(key);

    /// <summary>Assigns a key; refuses (returning the holder in <paramref name="conflict"/>)
    /// when reserved or already bound to a different action. Saves on success.</summary>
    public static bool TrySetKey(BindableAction action, Key key, out BindableAction? conflict)
    {
        conflict = null;
        if (IsReserved(key))
        {
            return false;
        }

        foreach (var entry in Current)
        {
            if (entry.Value == key && entry.Key != action)
            {
                conflict = entry.Key;
                return false;
            }
        }

        Current[action] = key;
        Save();
        return true;
    }

    public static void ResetToDefaults()
    {
        foreach (var spec in Specs)
        {
            Current[spec.Action] = spec.DefaultKey;
        }

        Save();
    }

    public static void Save()
    {
        var config = new ConfigFile();
        config.SetValue("meta", "version", 1);
        foreach (var spec in Specs)
        {
            config.SetValue(BindingsSection, spec.Id, Current[spec.Action].ToString());
        }

        config.Save(SavePath);
    }

    public static string LabelFor(BindableAction action) =>
        Specs.First(spec => spec.Action == action).Label;

    public static string DisplayName(Key key) =>
        key switch
        {
            Key.Period => ".",
            Key.Comma => ",",
            _ => OS.GetKeycodeString(key),
        };

    private static void Load()
    {
        var config = new ConfigFile();
        if (config.Load(SavePath) != Error.Ok)
        {
            return;
        }

        foreach (var spec in Specs)
        {
            if (!config.HasSectionKey(BindingsSection, spec.Id))
            {
                continue;
            }

            var raw = config.GetValue(BindingsSection, spec.Id).AsString();
            if (Enum.TryParse<Key>(raw, out var key) && !IsReserved(key))
            {
                Current[spec.Action] = key;
            }
        }

        // A hand-edited file could bind one key to two actions; later specs yield so every
        // key still maps to exactly one action.
        var seen = new HashSet<Key>();
        foreach (var spec in Specs)
        {
            if (!seen.Add(Current[spec.Action]))
            {
                Current[spec.Action] = spec.DefaultKey;
            }
        }
    }
}
