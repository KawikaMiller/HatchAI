using Xunit;

namespace HatchAI.Tests;

// The pixel pets. Three kinds of check:
//
//   - goldens: the pinned pictures in BuddySpriteGoldens, one per family and
//     stage and one per hat;
//   - the whole matrix — every species, stage, eye style and hat, at every
//     animation frame — fits the 24x24 grid without a single clipped pixel,
//     keeps both eyes whole, and draws the same thing twice;
//   - the palette: shiny and rarity change colours and nothing else.
public class BuddySpriteTests
{
    private static BuddyGenome Genome(
        BuddySpecies species,
        BuddyEyes eyes = BuddyEyes.Dot,
        BuddyHat hat = BuddyHat.None,
        BuddyRarity rarity = BuddyRarity.Common,
        bool shiny = false) =>
        new(species, BuddyTaxonomy.FamilyOf(species), rarity, shiny, eyes, hat,
            new BuddyStats(10, 20, 30, 40, 50), BuddyPersonality.Cheerful, BuddyPersonality.Zen, "Pip");

    private static string Ascii(BuddyGenome genome, BuddyStage stage, int frame = 0) =>
        BuddySprite.ToAscii(BuddySprite.Rasterize(genome, stage, frame));

    public static TheoryData<string> GoldenKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in BuddySpriteGoldens.Pictures.Keys) data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenKeys))]
    public void MatchesItsGolden(string key)
    {
        var parts = key.Split('/');
        string actual;
        if (parts[0] == "Hat")
        {
            actual = Ascii(Genome(BuddySpecies.Cat, hat: Enum.Parse<BuddyHat>(parts[1])), BuddyStage.Second);
        }
        else
        {
            var family = Enum.Parse<BuddyFamily>(parts[0]);
            var species = BuddySpriteGoldens.FamilySpecies.Single(s => BuddyTaxonomy.FamilyOf(s) == family);
            actual = Ascii(Genome(species), Enum.Parse<BuddyStage>(parts[1]));
        }

        Assert.True(
            BuddySpriteGoldens.Pictures[key].ReplaceLineEndings("\n") == actual,
            $"{key} no longer matches its golden. Now:\n{actual}");
    }

    [Fact]
    public void ThereIsAGoldenForEveryFamilyAtEveryStageAndEveryHat()
    {
        foreach (var family in Enum.GetValues<BuddyFamily>())
        {
            foreach (var stage in Enum.GetValues<BuddyStage>())
                Assert.Contains($"{family}/{stage}", BuddySpriteGoldens.Pictures.Keys);
        }

        foreach (var hat in Enum.GetValues<BuddyHat>().Where(h => h != BuddyHat.None))
            Assert.Contains($"Hat/{hat}", BuddySpriteGoldens.Pictures.Keys);
    }

    // 18 species x 5 stages x 6 eyes x 8 hats x 16 frames: 69,120 sprites
    // (55,296 before the egg). The egg ignores eyes and hat, so a sixth of
    // those are the same picture many times over — kept in the sweep anyway,
    // since "ignores" is itself something that could stop being true.
    // "Fits" means nothing was drawn off the edge and then dropped — which a
    // grid of the right length would never show on its own — and the top row
    // is clear at rest, so the bob loses nothing.
    [Fact]
    public void EveryCombinationFitsTheGridAtEveryFrame()
    {
        var drawn = 0;
        foreach (var species in Enum.GetValues<BuddySpecies>())
        foreach (var stage in Enum.GetValues<BuddyStage>())
        foreach (var eyes in Enum.GetValues<BuddyEyes>())
        foreach (var hat in Enum.GetValues<BuddyHat>())
        {
            var genome = Genome(species, eyes, hat, shiny: true);
            for (var frame = 0; frame < BuddySprite.FrameCount; frame++)
            {
                var grid = BuddySprite.Rasterize(genome, stage, frame, out var clipped);
                Assert.True(clipped == 0, $"{species} {stage} {eyes} {hat} frame {frame}: {clipped} pixels clipped\n{BuddySprite.ToAscii(grid)}");
                Assert.Equal(BuddySpriteGrid.Size, grid.Width);
                Assert.Equal(BuddySpriteGrid.Size, grid.Height);
                Assert.Equal(BuddySpriteGrid.Size * BuddySpriteGrid.Size, grid.Cells.Length);
                drawn++;
            }
        }

        // The count, so a stage or an enum member dropped from the sweep is a
        // failure here rather than a quietly smaller matrix.
        Assert.Equal(69_120, drawn);
    }

    // Both eyes are always whole: nothing drawn later covers one and neither
    // falls off the face. Counted against the pattern, frame 0 (open eyes).
    // An egg has no face yet, so it is held to the opposite: no eye at all.
    [Fact]
    public void BothEyesAreAlwaysWhole()
    {
        var perEye = new Dictionary<BuddyEyes, int>
        {
            [BuddyEyes.Dot] = 3, [BuddyEyes.Star] = 5, [BuddyEyes.Cross] = 5,
            [BuddyEyes.Ring] = 8, [BuddyEyes.Spiral] = 7, [BuddyEyes.Degree] = 4,
        };

        foreach (var species in Enum.GetValues<BuddySpecies>())
        foreach (var stage in Enum.GetValues<BuddyStage>())
        foreach (var eyes in Enum.GetValues<BuddyEyes>())
        foreach (var hat in Enum.GetValues<BuddyHat>())
        {
            var grid = BuddySprite.Rasterize(Genome(species, eyes, hat), stage, 0);
            // Eye only: EyeShine is also the aquatic crest's bubbles.
            var eyeCells = grid.Cells.Count(c => c == BuddyPixel.Eye);
            var expected = stage == BuddyStage.Egg ? 0 : perEye[eyes] * 2;
            Assert.True(expected == eyeCells, $"{species} {stage} {eyes} {hat}: {eyeCells} eye pixels\n{BuddySprite.ToAscii(grid)}");
        }
    }

    // ---- the egg ----------------------------------------------------------

    // Eyes and a hat are the buddy's, and it has not hatched: every eye style
    // under every hat is the same egg.
    [Fact]
    public void AnEggIgnoresEyesAndHat()
    {
        foreach (var species in Enum.GetValues<BuddySpecies>())
        {
            var eggs = Enum.GetValues<BuddyEyes>()
                .SelectMany(e => Enum.GetValues<BuddyHat>().Select(h => Ascii(Genome(species, e, h), BuddyStage.Egg)))
                .Distinct()
                .ToList();
            Assert.Single(eggs);
        }
    }

    // It rocks rather than bobbing: the top leans a pixel left, then right,
    // and back upright in between, while the rows it sits on never move — an
    // egg tips in its pot, it does not hop out of it.
    [Fact]
    public void AnEggRocksAndKeepsItsFooting()
    {
        foreach (var species in BuddySpriteGoldens.FamilySpecies)
        {
            var genome = Genome(species);
            var upright = BuddySprite.Rasterize(genome, BuddyStage.Egg, 0);
            var left = BuddySprite.Rasterize(genome, BuddyStage.Egg, 4);
            var right = BuddySprite.Rasterize(genome, BuddyStage.Egg, 12);

            Assert.Equal(BuddySprite.ToAscii(upright), Ascii(genome, BuddyStage.Egg, 8));
            Assert.NotEqual(BuddySprite.ToAscii(upright), BuddySprite.ToAscii(left));
            Assert.NotEqual(BuddySprite.ToAscii(left), BuddySprite.ToAscii(right));

            // Measured on the top row, which is the one that tips furthest.
            var top = Enumerable.Range(0, BuddySpriteGrid.Size).First(y => Enumerable.Range(0, BuddySpriteGrid.Size).Any(x => upright.At(x, y) != BuddyPixel.Transparent));
            Assert.Equal(LeftmostSolid(upright, top) - 1, LeftmostSolid(left, top));
            Assert.Equal(RightmostSolid(upright, top) + 1, RightmostSolid(right, top));

            for (var y = 16; y < BuddySpriteGrid.Size; y++)
            {
                for (var x = 0; x < BuddySpriteGrid.Size; x++)
                {
                    Assert.Equal(upright.At(x, y), left.At(x, y));
                    Assert.Equal(upright.At(x, y), right.At(x, y));
                }
            }
        }

        static int LeftmostSolid(BuddySpriteGrid g, int y) =>
            Enumerable.Range(0, g.Width).First(x => g.At(x, y) != BuddyPixel.Transparent);

        static int RightmostSolid(BuddySpriteGrid g, int y) =>
            Enumerable.Range(0, g.Width).Last(x => g.At(x, y) != BuddyPixel.Transparent);
    }

    // A shiny is visible before it hatches: the egg twinkles too.
    [Fact]
    public void AShinyEggTwinkles()
    {
        Assert.DoesNotContain(BuddyPixel.Sparkle, BuddySprite.Rasterize(Genome(BuddySpecies.Robot), BuddyStage.Egg, 0).Cells);
        Assert.Contains(BuddyPixel.Sparkle, BuddySprite.Rasterize(Genome(BuddySpecies.Robot, shiny: true), BuddyStage.Egg, 0).Cells);
    }

    // Six families, six eggs: none of them is another's with a new colour
    // slot, since the ASCII compares roles, not colours.
    [Fact]
    public void EveryFamilyLaysADifferentEgg()
    {
        var eggs = BuddySpriteGoldens.FamilySpecies.Select(s => Ascii(Genome(s), BuddyStage.Egg)).ToList();
        Assert.Equal(eggs.Count, eggs.Distinct().Count());
    }

    [Fact]
    public void RasterizingIsDeterministic()
    {
        var genome = Genome(BuddySpecies.Axolotl, BuddyEyes.Spiral, BuddyHat.Wizard, BuddyRarity.Legendary, shiny: true);

        Assert.Equal(Ascii(genome, BuddyStage.Third, 5), Ascii(genome, BuddyStage.Third, 5));
    }

    [Fact]
    public void FramesWrapInBothDirections()
    {
        var genome = Genome(BuddySpecies.Ghost);

        Assert.Equal(Ascii(genome, BuddyStage.First, 3), Ascii(genome, BuddyStage.First, 3 + BuddySprite.FrameCount * 7));
        Assert.Equal(Ascii(genome, BuddyStage.First, BuddySprite.FrameCount - 1), Ascii(genome, BuddyStage.First, -1));
    }

    [Fact]
    public void TheIdleLoopBobsAndBlinks()
    {
        var genome = Genome(BuddySpecies.Duck);
        var rest = BuddySprite.Rasterize(genome, BuddyStage.First, 0);
        var bob = BuddySprite.Rasterize(genome, BuddyStage.First, 4);
        var blink = BuddySprite.Rasterize(genome, BuddyStage.First, 14);

        // The bob is the rest frame one row higher, exactly.
        for (var y = 0; y < BuddySpriteGrid.Size - 1; y++)
        {
            for (var x = 0; x < BuddySpriteGrid.Size; x++) Assert.Equal(rest.At(x, y + 1), bob.At(x, y));
        }

        // A blink shuts the eyes: no glint, and fewer eye pixels than open.
        Assert.DoesNotContain(BuddyPixel.EyeShine, blink.Cells);
        Assert.True(blink.Cells.Count(c => c == BuddyPixel.Eye) < rest.Cells.Count(c => c is BuddyPixel.Eye or BuddyPixel.EyeShine));
        Assert.Contains(BuddyPixel.EyeShine, rest.Cells);
    }

    [Fact]
    public void OnlyAShinyTwinkles()
    {
        Assert.DoesNotContain(BuddyPixel.Sparkle, BuddySprite.Rasterize(Genome(BuddySpecies.Cat), BuddyStage.Second, 0).Cells);

        var a = BuddySprite.Rasterize(Genome(BuddySpecies.Cat, shiny: true), BuddyStage.Second, 0);
        var b = BuddySprite.Rasterize(Genome(BuddySpecies.Cat, shiny: true), BuddyStage.Second, 2);
        Assert.Contains(BuddyPixel.Sparkle, a.Cells);
        Assert.NotEqual(BuddySprite.ToAscii(a), BuddySprite.ToAscii(b));
    }

    [Fact]
    public void EveryEyeStyleAndEveryHatDrawsDifferently()
    {
        var eyes = Enum.GetValues<BuddyEyes>().Select(e => Ascii(Genome(BuddySpecies.Blob, e), BuddyStage.Second)).ToList();
        Assert.Equal(eyes.Count, eyes.Distinct().Count());

        var hats = Enum.GetValues<BuddyHat>().Select(h => Ascii(Genome(BuddySpecies.Blob, hat: h), BuddyStage.Second)).ToList();
        Assert.Equal(hats.Count, hats.Distinct().Count());
    }

    [Fact]
    public void EveryStageOfEverySpeciesIsItsOwnPicture()
    {
        foreach (var species in Enum.GetValues<BuddySpecies>())
        {
            var stages = Enum.GetValues<BuddyStage>().Select(s => Ascii(Genome(species), s)).ToList();
            Assert.Equal(5, stages.Distinct().Count());
        }

        var all = Enum.GetValues<BuddySpecies>().Select(s => Ascii(Genome(s), BuddyStage.First)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void AsciiHasALetterForEveryRole()
    {
        var letters = new HashSet<char>();
        foreach (var role in Enum.GetValues<BuddyPixel>())
        {
            var cells = new BuddyPixel[BuddySpriteGrid.Size * BuddySpriteGrid.Size];
            cells[0] = role;
            letters.Add(BuddySprite.ToAscii(new BuddySpriteGrid(BuddySpriteGrid.Size, BuddySpriteGrid.Size, cells))[0]);
        }

        Assert.Equal(Enum.GetValues<BuddyPixel>().Length, letters.Count);
    }

    // ---- palette ----------------------------------------------------------

    [Fact]
    public void AShinyHasADifferentPalette()
    {
        foreach (var species in Enum.GetValues<BuddySpecies>())
        {
            var plain = BuddySprite.Palette(Genome(species));
            var shiny = BuddySprite.Palette(Genome(species, shiny: true));
            Assert.NotEqual(plain[(int)BuddyPixel.Body], shiny[(int)BuddyPixel.Body]);
            // The face and cheeks stay put: a shiny is the same buddy recoloured.
            Assert.Equal(plain[(int)BuddyPixel.Eye], shiny[(int)BuddyPixel.Eye]);
            Assert.Equal(plain[(int)BuddyPixel.Blush], shiny[(int)BuddyPixel.Blush]);
        }
    }

    [Fact]
    public void RarityOnlyEverMakesAColourMoreVivid()
    {
        foreach (var species in Enum.GetValues<BuddySpecies>())
        {
            var spreads = Enum.GetValues<BuddyRarity>()
                .Select(r => Spread(BuddySprite.Palette(Genome(species, rarity: r))[(int)BuddyPixel.Body]))
                .ToList();
            for (var i = 1; i < spreads.Count; i++)
                Assert.True(spreads[i] >= spreads[i - 1], $"{species}: {string.Join(",", spreads)}");
            Assert.True(spreads[^1] > spreads[0], $"{species}: common and legendary look the same");
        }

        // max - min of the channels: a stand-in for saturation that needs no
        // colour library.
        static int Spread(uint argb)
        {
            int r = (int)(argb >> 16 & 0xFF), g = (int)(argb >> 8 & 0xFF), b = (int)(argb & 0xFF);
            return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        }
    }

    [Fact]
    public void EveryRoleIsOpaqueExceptTransparent()
    {
        foreach (var species in Enum.GetValues<BuddySpecies>())
        foreach (var hat in Enum.GetValues<BuddyHat>())
        {
            var palette = BuddySprite.Palette(Genome(species, hat: hat));
            Assert.Equal(Enum.GetValues<BuddyPixel>().Length, palette.Length);
            Assert.Equal(0u, palette[0] >> 24);
            Assert.All(palette.Skip(1), p => Assert.Equal(0xFFu, p >> 24));
        }
    }

    [Fact]
    public void AConstructsCoreGlowsCoolAndAFlowerWarm()
    {
        var robot = BuddySprite.Palette(Genome(BuddySpecies.Robot))[(int)BuddyPixel.Bloom];
        var cactus = BuddySprite.Palette(Genome(BuddySpecies.Cactus))[(int)BuddyPixel.Bloom];

        Assert.NotEqual(robot, cactus);
        Assert.True((robot & 0xFF) > (robot >> 16 & 0xFF), "the core should be blue-leaning");
    }
}
