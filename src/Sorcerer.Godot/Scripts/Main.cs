using Godot;
using Sorcerer.Core;
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
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _label.SetAnchorsPreset(LayoutPreset.FullRect);
        _label.AddThemeFontSizeOverride("font_size", 18);
        AddChild(_label);
    }

    private string RenderView()
    {
        var view = _session.View();
        return $"Sorcerer architecture stub\nTurn {view.Turn}\n"
            + "Godot is rendering a GameView from the shared GameSession.\n\n"
            + string.Join("\n", view.Messages);
    }
}

