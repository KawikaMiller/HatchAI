namespace HatchAI
{
    // The vocabulary a buddy is made of (CB-195). These enums are the creature
    // table the owner approved "as drafted" in the plan on CB-195 — recalled
    // from the April Fools /buddy and explicitly NOT verified against it, so
    // treat every list here as a product decision rather than a fact about
    // the original.
    //
    // Declaration order is load-bearing. BuddyHatch maps hash bytes onto these
    // by index, and its golden tests pin that mapping for real users' seeds:
    // inserting a member anywhere but the end silently gives existing users a
    // different buddy. Append only, and only with a new hatch version salt.

    internal enum BuddySpecies
    {
        // birds
        Duck, Goose, Owl, Penguin,
        // critters
        Cat, Rabbit, Capybara, Chonk,
        // aquatic
        Octopus, Axolotl, Turtle, Snail,
        // mythic
        Dragon, Ghost,
        // flora
        Cactus, Mushroom,
        // constructs
        Robot, Blob,
    }

    // Evolution art — and the egg's — is drawn per family rather than per
    // species, which is what keeps eighteen species times five stages a
    // tractable amount of drawing.
    internal enum BuddyFamily { Birds, Critters, Aquatic, Mythic, Flora, Constructs }

    // Weights 60/25/10/4/1 percent; stat floors 5/15/25/35/50. Common wears no
    // hat. The numbers live in BuddyHatch/BuddyTaxonomy, not here.
    internal enum BuddyRarity { Common, Uncommon, Rare, Epic, Legendary }

    internal enum BuddyEyes { Dot, Star, Cross, Ring, Spiral, Degree }

    internal enum BuddyHat { None, Crown, TopHat, Propeller, Halo, Wizard, Beanie, TinyDuck }

    // Each 0-100. One peak and one dump stat per buddy.
    internal enum BuddyStat { Debugging, Patience, Chaos, Wisdom, Snark }

    internal enum BuddyPersonality { Cheerful, Grumpy, Sleepy, Curious, Sassy, Anxious, Zen, Chaotic }

    // An egg, which hatches at 2,500 output tokens, then a hatchling that
    // evolves every 10,000 after that: 12.5k, 22.5k, 32.5k (thresholds in
    // BuddyProgress). Third is the last stage; beyond it progress shows as the
    // rebirth offer and veteran stars, not as a fourth evolution.
    //
    // Unlike the enums above, this one's order is not a hatch input: a stage
    // is derived from the token count every time it is read and never hashed.
    // The one place it is stored, a history entry, stores it by name — so
    // inserting Egg ahead of Hatchling (CB-195) moved no one's stored stage.
    internal enum BuddyStage { Egg, Hatchling, First, Second, Third }

    // The tables behind the enums above, owned by E1 (CB-195).
    internal static class BuddyTaxonomy
    {
        // Mirrors the grouping comments in BuddySpecies. A switch rather than
        // arithmetic on the index, so appending a species to the enum without
        // giving it a family is a thrown exception on its first hatch (and a
        // failing EverySpeciesHasAFamily test) instead of a buddy quietly
        // drawn with some other family's evolution art.
        internal static BuddyFamily FamilyOf(BuddySpecies species) => species switch
        {
            BuddySpecies.Duck or BuddySpecies.Goose or BuddySpecies.Owl or BuddySpecies.Penguin
                => BuddyFamily.Birds,
            BuddySpecies.Cat or BuddySpecies.Rabbit or BuddySpecies.Capybara or BuddySpecies.Chonk
                => BuddyFamily.Critters,
            BuddySpecies.Octopus or BuddySpecies.Axolotl or BuddySpecies.Turtle or BuddySpecies.Snail
                => BuddyFamily.Aquatic,
            BuddySpecies.Dragon or BuddySpecies.Ghost => BuddyFamily.Mythic,
            BuddySpecies.Cactus or BuddySpecies.Mushroom => BuddyFamily.Flora,
            BuddySpecies.Robot or BuddySpecies.Blob => BuddyFamily.Constructs,
            _ => throw new ArgumentOutOfRangeException(nameof(species), species, "no family for this species"),
        };

        // The lowest any stat can roll at each rarity, from the plan: 5, 15,
        // 25, 35, 50. It is the whole of what rarity means for stats — a
        // legendary is not guaranteed a higher peak, only a higher floor.
        internal static int StatFloor(BuddyRarity rarity) => rarity switch
        {
            BuddyRarity.Common => 5,
            BuddyRarity.Uncommon => 15,
            BuddyRarity.Rare => 25,
            BuddyRarity.Epic => 35,
            BuddyRarity.Legendary => 50,
            _ => throw new ArgumentOutOfRangeException(nameof(rarity), rarity, "no stat floor for this rarity"),
        };
    }
}
