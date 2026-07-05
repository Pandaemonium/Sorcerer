using Godot;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// A full-rect canvas that delegates its draw pass to its owner. The minigame stacks two of
/// these - a normal-blend ink layer and an additive glow layer - so embers and strokes can
/// composite like light while vignette and ash composite like paint, without scene assets.
/// </summary>
public partial class DrawLayer : Control
{
    public event Action<DrawLayer>? Painted;

    public override void _Draw() => Painted?.Invoke(this);
}
