namespace Sorcerer.Core.Runtime;

/// <summary>
/// Phase 2.6: how a defeated body is disposed of, in the register of whoever landed the killing
/// blow. The provenance is recorded on the body at the moment of the strike
/// (<see cref="Sorcerer.Core.World.GameState.LastControlledDamageProvenance"/>); this turns it into
/// a treatment tag and a short, worldly disposition line. Shared by the defeat narration in
/// <see cref="Sorcerer.Core.GameSession"/> and by <see cref="RunChronicle"/> so the chronicle and
/// the moment of death read the same register. Deterministic; provider prose can enrich later.
/// </summary>
public static class DeathTreatment
{
    public const string None = "none";
    public const string Imperial = "imperial";
    public const string Wild = "wild";
    public const string Mortal = "mortal";

    /// <summary>
    /// The treatment for a defeat, from the killer's recorded provenance. An unrecorded blow
    /// (or one that named no register) falls through to an ordinary <see cref="Mortal"/> death.
    /// </summary>
    public static string ForDefeat(string? provenance) => (provenance ?? Mortal) switch
    {
        Imperial => Imperial,
        Wild => Wild,
        _ => Mortal,
    };

    /// <summary>
    /// A short disposition line for the fallen body, narrated in the killer's register. Every line
    /// opens on the same fall so the moment reads consistently however the body is then handled.
    /// </summary>
    public static string Disposition(string treatment) => treatment switch
    {
        Imperial =>
            "Your body falls. The Censorate is already there with forms and a wax seal, "
            + "filing the incident before the blood has cooled.",
        Wild =>
            "Your body falls. What the wild left in it will not lie still; by morning it has "
            + "walked off wearing your shape and a stranger's errands.",
        _ =>
            "Your body falls. Somewhere, the world begins arranging a stranger's dawn.",
    };
}
