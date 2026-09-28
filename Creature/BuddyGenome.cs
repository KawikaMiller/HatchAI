namespace HatchAI
{
    // Everything a hatch decides about a buddy, and nothing it earns. A pure
    // function of (uuid, rebirths) via BuddyHatch.Roll, so it is never
    // persisted as the source of truth — it is re-rolled on every load. The
    // history keeps a snapshot of it instead (BuddyHistoryEntry), so a past
    // buddy survives a later change to the hatch mapping.
    //
    // Name is the rolled default name. A user-chosen name lives on BuddyState
    // and wins when set; the genome never changes after a hatch.
    internal sealed record BuddyGenome(
        BuddySpecies Species,
        BuddyFamily Family,
        BuddyRarity Rarity,
        bool Shiny,
        BuddyEyes Eyes,
        BuddyHat Hat,
        BuddyStats Stats,
        BuddyPersonality Primary,
        BuddyPersonality Secondary,
        string Name);

    // A value type with one field per BuddyStat rather than a dictionary, so
    // two genomes rolled from the same seed compare equal and a golden test
    // can assert a whole genome in one line. Keep the fields in BuddyStat's
    // order.
    internal readonly record struct BuddyStats(
        int Debugging,
        int Patience,
        int Chaos,
        int Wisdom,
        int Snark);
}
