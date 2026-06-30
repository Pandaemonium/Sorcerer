using Godot;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
using Sorcerer.Magic;

namespace Sorcerer.Godot;

public partial class Main : Control
{
    private GameSession _session = null!;
    private Label _label = null!;

    public override void _Ready()
    {
        _session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()));

        _label = new Label
        {
            Text = RenderView(),
            AutowrapMode = TextServer.AutowrapMode.Off,
        };
        _label.SetAnchorsPreset(LayoutPreset.FullRect);
        _label.AddThemeFontSizeOverride("font_size", 18);
        AddChild(_label);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }

        var command = CommandForKey(key.Keycode);
        if (command is null)
        {
            return;
        }

        _ = ExecuteAsync(command);
        GetViewport().SetInputAsHandled();
    }

    private string RenderView()
    {
        var view = _session.View();
        return RenderMap(view)
            + "\n"
            + $"Turn {view.Turn}  Controlled: {view.ControlledEntityId}\n"
            + string.Join("\n", view.Messages.TakeLast(8));
    }

    private async Task ExecuteAsync(GameCommand command)
    {
        await _session.ExecuteAsync(command);
        _label.Text = RenderView();
    }

    private static GameCommand? CommandForKey(Key key) =>
        key switch
        {
            Key.Up or Key.K => new MoveCommand(Direction.North),
            Key.Down or Key.J => new MoveCommand(Direction.South),
            Key.Left or Key.H => new MoveCommand(Direction.West),
            Key.Right or Key.L => new MoveCommand(Direction.East),
            Key.Y => new MoveCommand(Direction.NorthWest),
            Key.U => new MoveCommand(Direction.NorthEast),
            Key.B => new MoveCommand(Direction.SouthWest),
            Key.N => new MoveCommand(Direction.SouthEast),
            Key.Period => new WaitCommand(),
            Key.I => new InspectCommand(),
            Key.C => new CastCommand("strike the nearest imperial soldier", CastPerformance.Neutral),
            _ => null,
        };

    private static string RenderMap(GameView view)
    {
        var glyphs = new char[view.Height, view.Width];
        for (var y = 0; y < view.Height; y++)
        {
            for (var x = 0; x < view.Width; x++)
            {
                glyphs[y, x] = '.';
            }
        }

        foreach (var tile in view.Tiles ?? Array.Empty<MapTileCard>())
        {
            glyphs[tile.Y, tile.X] = tile.BlocksMovement ? '#' : '.';
        }

        foreach (var entity in view.Entities.OrderBy(entity => entity.Id == view.ControlledEntityId ? 1 : 0))
        {
            if (entity.X >= 0 && entity.Y >= 0 && entity.X < view.Width && entity.Y < view.Height)
            {
                glyphs[entity.Y, entity.X] = entity.Glyph;
            }
        }

        var lines = new List<string>();
        for (var y = 0; y < view.Height; y++)
        {
            var chars = new char[view.Width];
            for (var x = 0; x < view.Width; x++)
            {
                chars[x] = glyphs[y, x];
            }

            lines.Add(new string(chars));
        }

        return string.Join("\n", lines);
    }
}
