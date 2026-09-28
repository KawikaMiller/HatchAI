using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HatchAI
{
    // Turns (uuid, rebirths) into a buddy. Owned by E1 (CB-195).
    //
    // SHA-256 over the UTF-8 of "claude-buddy/hatch/v1|{uuid}|{rebirths}",
    // with rebirths in invariant culture. Never string.GetHashCode (randomised
    // per process on .NET) and never System.Random (its sequence is not a
    // documented contract across runtime versions): either would quietly give
    // a user a different buddy after an upgrade. Golden tests pin the mapping
    // (three uuids x rebirths 0-3), so changing anything here — including the
    // order of any enum in BuddyTaxonomy — means a new salt version, not an
    // edit.
    //
    // The 32-byte digest is read as sixteen big-endian 16-bit rolls, one per
    // decision, each used for exactly one thing. Independent slices rather
    // than one number carved up by division, so the species a buddy gets says
    // nothing about its eyes, and so adding a decision in a v2 cannot shift
    // the ones before it.
    internal static class BuddyHatch
    {
        internal const string Salt = "claude-buddy/hatch/v1";

        // Cumulative rarity cut-offs in basis points (of 10,000): common 60%,
        // uncommon 25%, rare 10%, epic 4%, legendary the remaining 1%.
        internal static readonly int[] RarityCutoffs = { 6_000, 8_500, 9_500, 9_900 };

        // 1 in 100, rolled on its own slice, so any rarity can be shiny.
        internal const int ShinyPercent = 1;

        // How many members of each enum v1 can reach. Pinned here rather than
        // read off the enum, so appending a species or a hat does not re-scale
        // every existing user's roll — the new member is simply unreachable
        // until a v2 salt opts into it. HatchCountsMatchTheEnums fails the day
        // they drift, which is the point at which somebody has to decide.
        internal const int SpeciesCount = 18;
        internal const int EyesCount = 6;
        internal const int HatCount = 7;        // excluding None
        internal const int StatCount = 5;
        internal const int PersonalityCount = 8;

        // Stat bands. The peak always outranks every ordinary stat and every
        // ordinary stat outranks the dump, at every rarity: peak 75-100,
        // ordinary floor+16 to 74, dump floor to floor+15. That strict order is
        // what makes "one peak, one dump" true of every buddy rather than of
        // most, and it holds even at legendary, whose ordinary band narrows to
        // 66-74 because its floor is already 50.
        internal const int PeakMin = 75;
        internal const int OrdinaryMax = 74;
        internal const int DumpSpan = 16;

        // Two syllables, one from each table, make the default name: "Pipkin",
        // "Wobbly". Sixteen each, so a name costs exactly four bits a side.
        internal static readonly string[] FirstSyllables =
        {
            "Pip", "Bo", "Mo", "Zu", "Ki", "Lu", "Fen", "Bix",
            "Dot", "Nim", "Wob", "Tam", "Rue", "Gus", "Ola", "Juni",
        };

        internal static readonly string[] SecondSyllables =
        {
            "kin", "bo", "ly", "zle", "ster", "mo", "pop", "ber",
            "dle", "ni", "wick", "sy", "by", "ra", "to", "go",
        };

        // Slice indices, in the order the digest is read. Part of the mapping.
        private const int RarityRoll = 0, ShinyRoll = 1, SpeciesRoll = 2, EyesRoll = 3, HatRoll = 4,
            PeakRoll = 5, DumpRoll = 6, FirstStatRoll = 7, PrimaryRoll = 12, SecondaryRoll = 13,
            FirstSyllableRoll = 14, SecondSyllableRoll = 15;

        internal static BuddyGenome Roll(string uuid, int rebirths)
        {
            ArgumentNullException.ThrowIfNull(uuid);
            ArgumentOutOfRangeException.ThrowIfNegative(rebirths);

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{Salt}|{uuid}|{rebirths.ToString(CultureInfo.InvariantCulture)}"));
            int Slice(int i) => (digest[2 * i] << 8) | digest[2 * i + 1];

            var rarity = RarityFrom(Scale(Slice(RarityRoll), 10_000));
            var shiny = Scale(Slice(ShinyRoll), 100) < ShinyPercent;
            var species = (BuddySpecies)Scale(Slice(SpeciesRoll), SpeciesCount);
            var eyes = (BuddyEyes)Scale(Slice(EyesRoll), EyesCount);
            // Common wears no hat; the roll is still read so the slice layout
            // does not depend on rarity.
            var hatRoll = Scale(Slice(HatRoll), HatCount);
            var hat = rarity == BuddyRarity.Common ? BuddyHat.None : (BuddyHat)(1 + hatRoll);

            var peak = Scale(Slice(PeakRoll), StatCount);
            var dump = SkipOver(Scale(Slice(DumpRoll), StatCount - 1), peak);
            var floor = BuddyTaxonomy.StatFloor(rarity);
            var values = new int[StatCount];
            for (var s = 0; s < StatCount; s++)
            {
                var x = Slice(FirstStatRoll + s);
                values[s] = s == peak ? PeakMin + Scale(x, 100 - PeakMin + 1)
                    : s == dump ? floor + Scale(x, DumpSpan)
                    : floor + DumpSpan + Scale(x, OrdinaryMax - (floor + DumpSpan) + 1);
            }

            var primary = Scale(Slice(PrimaryRoll), PersonalityCount);
            var secondary = SkipOver(Scale(Slice(SecondaryRoll), PersonalityCount - 1), primary);

            var name = FirstSyllables[Scale(Slice(FirstSyllableRoll), FirstSyllables.Length)]
                + SecondSyllables[Scale(Slice(SecondSyllableRoll), SecondSyllables.Length)];

            return new BuddyGenome(
                species, BuddyTaxonomy.FamilyOf(species), rarity, shiny, eyes, hat,
                new BuddyStats(values[0], values[1], values[2], values[3], values[4]),
                (BuddyPersonality)primary, (BuddyPersonality)secondary, name);
        }

        internal static BuddyRarity RarityFrom(int basisPoints)
        {
            var r = 0;
            while (r < RarityCutoffs.Length && basisPoints >= RarityCutoffs[r]) r++;
            return (BuddyRarity)r;
        }

        // A 16-bit roll onto 0..n-1 by multiply-and-shift rather than modulo:
        // every bucket gets floor or ceil of 65536/n values, so no bucket is
        // favoured by more than one value in 65,536.
        private static int Scale(int roll, int n) => (int)(((long)roll * n) >> 16);

        // Picks among the n-1 values that are not `taken`, so a buddy's dump
        // stat is never its peak and its two personalities never match.
        private static int SkipOver(int pick, int taken) => pick >= taken ? pick + 1 : pick;
    }
}
