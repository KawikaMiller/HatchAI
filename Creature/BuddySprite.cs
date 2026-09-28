namespace HatchAI
{
    // What one cell of a sprite is — a role, not a colour. BuddySpriteControl
    // maps roles to brushes (shiny and rarity change the mapping, not the
    // grid), which keeps the grid a thing an ASCII golden test can pin.
    // E3 may append roles; value 0 must stay Transparent.
    internal enum BuddyPixel : byte
    {
        Transparent = 0,
        Outline,
        Body,
        Shade,
        Belly,
        Eye,
        Accent,
        Hat,
        HatShade,
        Sparkle,

        // Appended by E3. The white glint in an eye; without it a 2x2 black
        // eye reads as a hole rather than as something looking at you.
        EyeShine,
        // Cheeks. Pink on every species, which is most of what makes a
        // blob of pixels read as a pet rather than as an icon.
        Blush,
        // A hat's third colour: a crown's gem, a top hat's band.
        HatAccent,
        // What a hatchling sits in or on — eggshell, puddle, flowerpot —
        // coloured by family rather than by species. Also the shell of a
        // bird's, a mythic's and an aquatic's egg, so a bird or mythic
        // hatchling's broken shell is recognisably the egg it came from.
        Prop,
        PropShade,
        // Flora's leaves, which have to stay green on a red mushroom.
        Leaf,
        // Flora's final-stage flower, and a construct's glowing core.
        Bloom,
    }

    // Row-major, Width x Height, Cells.Length == Width * Height. An array does
    // not give a record value equality, so compare grids through their ASCII
    // rendering in tests, not with ==.
    internal sealed record BuddySpriteGrid(int Width, int Height, BuddyPixel[] Cells)
    {
        internal const int Size = 24;

        internal BuddyPixel At(int x, int y) => Cells[y * Width + x];
    }

    // Owned by E3 (CB-195). Pure: no Avalonia, no window. Drawn in code, no
    // bitmaps (owner decision). `frame` is the idle-animation frame; the
    // caller wraps it, so any int is valid.
    //
    // How a buddy is built, in the order it is drawn:
    //
    //   1. the species' silhouette, sized by stage, plus the family's
    //      evolution parts (wings, tails, fins, leaves, bolts)
    //   2. a one-pixel shade along every lower-right edge of the body
    //   3. belly, markings and props
    //   4. the hat, or — only when there is no hat — the family's final-stage
    //      crown (crest, fin, flower, antennae), so the two never fight over
    //      the top of the head
    //   5. the face, last of the solid layers, so no hat or part can ever
    //      cover an eye
    //   6. a dark outline around everything, computed rather than drawn, so
    //      every shape above gets one without having to remember to
    //   7. the idle bob, then shiny sparkles, which do not bob
    //
    // Families, not species, decide what an evolution adds (the plan's
    // per-family table — eighteen species times four stages would be seventy-
    // two drawings; six families times three evolutions is eighteen parts):
    //
    //   family      hatchling          2nd evolution    3rd evolution
    //   birds       in its eggshell    wings            bigger wings, crest
    //   critters    small, no extras   tail             bigger tail, cheek tufts
    //   aquatic     on a puddle        side fins        crest fin, bubbles
    //   mythic      in a speckled egg  small wings      big wings, horns
    //   flora       in a pot           two leaves       leaves, a flower
    //   constructs  one antenna        side bolts       arms, glowing core
    //
    // The first evolution is the species itself at full size with no extras,
    // which is the one every later stage is recognisably a version of.
    //
    // Before all of those comes the egg (CB-195), which is not steps 1-7 at
    // all but one shape drawn by DrawEgg, flavoured by family:
    //
    //   birds       cream shell, speckled in the species' accent colour
    //   critters    the species' own colours, in patches
    //   aquatic     a jelly egg on its puddle, the little one visible inside
    //   mythic      the speckled green shell the hatchling later sits in
    //   flora       a seed in the species' colours, sprouting, in its pot
    //   constructs  a capsule with a riveted seam and a glowing porthole
    internal static class BuddySprite
    {
        // Frames in one idle loop. At the four frames a second BuddyWindow
        // ticks at, that is a bob every second and a blink every four.
        internal const int FrameCount = 16;

        internal static BuddySpriteGrid Rasterize(BuddyGenome genome, BuddyStage stage, int frame) =>
            Rasterize(genome, stage, frame, out _);

        // `clipped` counts every solid pixel that fell outside the grid. It
        // is the test's way of asking "does every combination actually fit in
        // 24x24", which a grid of the right length always appears to.
        internal static BuddySpriteGrid Rasterize(BuddyGenome genome, BuddyStage stage, int frame, out int clipped)
        {
            var f = ((frame % FrameCount) + FrameCount) % FrameCount;
            var blink = f == 14;
            var bob = f / 4 % 2 == 1;

            var c = new Canvas();

            // An egg is its own drawing — no face, no hat, no evolution parts
            // — and rocks rather than bobbing; see DrawEgg.
            if (stage == BuddyStage.Egg)
            {
                DrawEgg(c, genome, f);
                c.Outline();
                if (genome.Shiny) DrawSparkles(c, f);
                clipped = c.Clipped;
                return new BuddySpriteGrid(BuddySpriteGrid.Size, BuddySpriteGrid.Size, c.Cells);
            }

            // Everything below counts stages from the hatchling, which is
            // where the drawing started before the egg was added ahead of it
            // (CB-195): 0 is the hatchling, 3 the third evolution.
            var s = (int)stage - 1;
            var layout = Layout.For(genome.Family, s);

            var face = DrawSpecies(c, genome.Species, layout);
            DrawFamilyParts(c, genome, layout, face);

            c.ShadeBody();
            DrawMarkings(c, genome.Species, layout, face);
            DrawProp(c, genome, layout);

            var headTop = c.TopOfColumns(11, 12);
            if (genome.Hat != BuddyHat.None)
            {
                DrawHat(c, genome.Hat, headTop);
            }
            else
            {
                DrawCrown(c, genome, layout, headTop);
            }

            DrawFace(c, genome, face, blink);

            c.Outline();
            if (bob) c.ShiftUp();

            if (genome.Shiny) DrawSparkles(c, f);

            clipped = c.Clipped;
            return new BuddySpriteGrid(BuddySpriteGrid.Size, BuddySpriteGrid.Size, c.Cells);
        }

        // One character per role, for golden tests and for reading a sprite
        // in a failure message. Rows joined with '\n'.
        internal static string ToAscii(BuddySpriteGrid grid)
        {
            var rows = new string[grid.Height];
            for (var y = 0; y < grid.Height; y++)
            {
                var row = new char[grid.Width];
                for (var x = 0; x < grid.Width; x++) row[x] = AsciiFor(grid.At(x, y));
                rows[y] = new string(row);
            }

            return string.Join('\n', rows);
        }

        private static char AsciiFor(BuddyPixel pixel) => pixel switch
        {
            BuddyPixel.Transparent => '.',
            BuddyPixel.Outline => '#',
            BuddyPixel.Body => 'o',
            BuddyPixel.Shade => 's',
            BuddyPixel.Belly => 'b',
            BuddyPixel.Eye => 'e',
            BuddyPixel.Accent => 'a',
            BuddyPixel.Hat => 'h',
            BuddyPixel.HatShade => 'H',
            BuddyPixel.Sparkle => '*',
            BuddyPixel.EyeShine => 'w',
            BuddyPixel.Blush => 'p',
            BuddyPixel.HatAccent => 'g',
            BuddyPixel.Prop => 'r',
            BuddyPixel.PropShade => 'R',
            BuddyPixel.Leaf => 'l',
            _ => 'f', // Bloom — the last role; a new one needs its own letter
        };

        // ---- layout -----------------------------------------------------

        // The body's ellipse for a stage. Everything is measured from the
        // grid's centre line x = 12 (between cells 11 and 12), so a shape
        // drawn about it comes out mirror-symmetric in whole pixels.
        private readonly record struct Layout(int Stage, double Cx, double Cy, double Rx, double Ry, double Bottom, bool InEgg)
        {
            private static readonly double[] Rxs = { 5.0, 6.5, 7.5, 8.5 };
            private static readonly double[] Rys = { 4.5, 5.5, 6.5, 7.5 };

            internal static Layout For(BuddyFamily family, int stage)
            {
                // The lowest body row is 21, leaving row 22 for the outline and
                // row 23 for feet and props. A hatchling in an eggshell or a
                // pot sits lower, so the prop can cover its bottom half.
                var bottom = 21.5;
                if (stage == 0 && family == BuddyFamily.Aquatic) bottom = 20.5;
                if (family == BuddyFamily.Flora) bottom = stage == 0 ? 21.0 : 20.0;

                var rx = Rxs[stage];
                var ry = Rys[stage];
                var inEgg = stage == 0 && family is BuddyFamily.Birds or BuddyFamily.Mythic;
                return new Layout(stage, 12, bottom - ry, rx, ry, bottom, inEgg);
            }
        }

        // Where a face goes: the ellipse it is centred in, and what kind of
        // mouth it wears.
        private readonly record struct Face(double Cx, double Cy, double Rx, double Ry, Mouth Mouth);

        private enum Mouth { Smile, Bill, Beak, Snout, Screen, Cat }

        // A face narrower than this cannot fit two 3x3 eyes with a gap, so a
        // small head is widened to it. It is the one size rule every species
        // shares, and it is why a hatchling is mostly face.
        private const double MinFaceRx = 4.5;

        // Half away from zero. Math.Round's default is banker's rounding,
        // which sends 6.5 to 6 and 16.5 to 16 — and a shape whose left edge
        // rounds one way and right edge the other is a lopsided buddy.
        private static int R(double v) => (int)Math.Floor(v + 0.5);

        private static Face FaceIn(double cx, double cy, double rx, double ry, Mouth mouth) =>
            new(cx, cy, Math.Max(MinFaceRx, rx), ry, mouth);

        // ---- species ----------------------------------------------------

        private static Face DrawSpecies(Canvas c, BuddySpecies species, Layout l)
        {
            var (cx, cy, rx, ry) = (l.Cx, l.Cy, l.Rx, l.Ry);
            var s = l.Stage;
            const BuddyPixel O = BuddyPixel.Body;

            switch (species)
            {
                case BuddySpecies.Duck:
                    c.Ellipse(cx, cy, rx, ry, O);
                    Feet(c, l, BuddyPixel.Accent);
                    return FaceIn(cx, cy - ry * 0.1, rx, ry, Mouth.Bill);

                case BuddySpecies.Goose:
                {
                    // Taller and narrower than the duck, with a neck: a head
                    // riding on a body rather than one round shape.
                    var bodyRy = ry * 0.7;
                    var bodyCy = l.Bottom - bodyRy;
                    c.Ellipse(cx, bodyCy, rx, bodyRy, O);
                    var headRy = Math.Max(3.5, ry * 0.55);
                    var headCy = bodyCy - bodyRy * 0.55;
                    c.Ellipse(cx, headCy - (s == 0 ? 0 : 1), Math.Max(MinFaceRx, rx * 0.7), headRy, O);
                    Feet(c, l, BuddyPixel.Accent);
                    return FaceIn(cx, headCy - (s == 0 ? 0 : 1), rx * 0.7, headRy, Mouth.Bill);
                }

                case BuddySpecies.Owl:
                    c.Ellipse(cx, cy, rx, ry * 1.05, O);
                    // Ear tufts: two little horns of feathers.
                    Mirror(c, cx, cy - ry * 1.05 + 1, R(rx * 0.55), new[] { "o.", "oo" }, O);
                    return FaceIn(cx, cy - ry * 0.1, rx, ry, Mouth.Beak);

                case BuddySpecies.Penguin:
                    c.Ellipse(cx, cy, rx * 0.95, ry * 1.05, O);
                    // Flippers, held out a little.
                    Mirror(c, cx, cy + 1, R(rx * 0.95), new[] { "o", "o", "o" }, O);
                    Feet(c, l, BuddyPixel.Accent);
                    return FaceIn(cx, cy - ry * 0.2, rx, ry, Mouth.Beak);

                case BuddySpecies.Cat:
                    c.Ellipse(cx, cy, rx, ry, O);
                    Ears(c, cx, cy - ry, rx, pointy: true);
                    return FaceIn(cx, cy, rx, ry, Mouth.Cat);

                case BuddySpecies.Rabbit:
                {
                    c.Ellipse(cx, cy, rx, ry, O);
                    // Long ears, straight up, with a pink inside.
                    var earH = 3 + Math.Min(s, 2);
                    var top = (int)Math.Floor(cy - ry) - earH + 1;
                    for (var y = top; y < top + earH + 1; y++)
                    {
                        c.Mirrored(cx, 2, y, O);
                        c.Mirrored(cx, 3, y, O);
                    }

                    for (var y = top + 1; y < top + earH; y++) c.Mirrored(cx, 2, y, BuddyPixel.Accent);
                    return FaceIn(cx, cy + ry * 0.05, rx, ry, Mouth.Cat);
                }

                case BuddySpecies.Capybara:
                    // Wide, low and square-ish: an ellipse flattened into a
                    // loaf, with two tiny round ears.
                    c.Ellipse(cx, cy + ry * 0.12, rx * 1.15, ry * 0.88, O);
                    c.Rect((int)(cx - rx * 0.9), (int)Math.Ceiling(cy), (int)(cx + rx * 0.9) - 1, (int)(l.Bottom - 0.5), O);
                    Mirror(c, cx, cy - ry * 0.76 - 0.5, R(rx * 0.7), new[] { "oo" }, O);
                    return FaceIn(cx, cy - ry * 0.05, rx, ry, Mouth.Snout);

                case BuddySpecies.Chonk:
                    // Every bit as round as it sounds.
                    c.Ellipse(cx, cy + ry * 0.05, rx * 1.2, ry * 0.95, O);
                    Ears(c, cx, cy - ry * 0.9 + 0.5, rx * 1.15, pointy: false);
                    return FaceIn(cx, cy, rx, ry, Mouth.Cat);

                case BuddySpecies.Octopus:
                {
                    // A dome of a head over a row of legs: the bottom rows are
                    // cut into tentacles with a gap every third column.
                    c.Ellipse(cx, cy - ry * 0.1, rx, ry * 0.9, O);
                    var legTop = (int)Math.Floor(cy + ry * 0.3);
                    var half = (int)Math.Floor(rx) - 1;
                    c.Rect(12 - half, legTop, 11 + half, (int)(l.Bottom - 0.5), O);
                    for (var y = legTop + 1; y <= (int)(l.Bottom - 0.5); y++)
                    {
                        for (var x = 0; x < BuddySpriteGrid.Size; x++)
                        {
                            // Distance from the centre line, the same on both
                            // sides, so the legs come out symmetric.
                            var d = x < 12 ? 11 - x : x - 12;
                            if (c[x, y] == O && d % 3 == 1) c[x, y] = BuddyPixel.Transparent;
                        }
                    }

                    return FaceIn(cx, cy - ry * 0.25, rx, ry * 0.9, Mouth.Smile);
                }

                case BuddySpecies.Axolotl:
                {
                    // A wide, flat head with three feathery gills either side.
                    c.Ellipse(cx, cy + ry * 0.1, rx * 1.05, ry * 0.9, O);
                    var gx = R(rx * 1.05);
                    var gy = cy - ry * 0.2;
                    for (var i = 0; i < 3; i++)
                    {
                        var y = R(gy) - 2 + i * 2;
                        c.Mirrored(cx, gx, y, BuddyPixel.Accent);
                        c.Mirrored(cx, gx + 1, y - 1 + i, BuddyPixel.Accent);
                    }

                    return FaceIn(cx, cy + ry * 0.05, rx, ry, Mouth.Smile);
                }

                case BuddySpecies.Turtle:
                {
                    // Front on: a domed shell behind, the head in front of it,
                    // and four stubby feet.
                    var shellCy = cy + ry * 0.15;
                    c.Ellipse(cx, shellCy, rx * 1.1, ry * 0.85, BuddyPixel.Accent);
                    Feet(c, l, O, wide: true);
                    var headRy = Math.Max(3.2, ry * 0.55);
                    var headCy = cy - ry * 0.2;
                    c.Ellipse(cx, headCy, Math.Max(MinFaceRx, rx * 0.62), headRy, O);
                    return FaceIn(cx, headCy, rx * 0.62, headRy, Mouth.Smile);
                }

                case BuddySpecies.Snail:
                {
                    // A shell up behind, a soft body in front of it, and two
                    // stalks with nobs on — the eyes stay on the face, where
                    // the eye styles can be read.
                    var shellCy = cy - ry * 0.15;
                    c.Ellipse(cx, shellCy, rx * 0.95, ry * 0.85, BuddyPixel.Accent);
                    var headRy = Math.Max(3.2, ry * 0.5);
                    var headCy = l.Bottom - headRy;
                    c.Ellipse(cx, headCy, Math.Max(MinFaceRx, rx * 1.05), headRy, O);
                    return FaceIn(cx, headCy, rx * 0.8, headRy, Mouth.Smile);
                }

                case BuddySpecies.Dragon:
                    c.Ellipse(cx, cy, rx, ry, O);
                    // Nubbly horns at every stage; mythic's third evolution
                    // grows them.
                    Mirror(c, cx, cy - ry, R(rx * 0.55), new[] { "a" }, BuddyPixel.Accent);
                    Feet(c, l, O);
                    return FaceIn(cx, cy - ry * 0.05, rx, ry, Mouth.Snout);

                case BuddySpecies.Ghost:
                {
                    // A dome with a straight drop to a scalloped hem.
                    c.Ellipse(cx, cy - ry * 0.1, rx, ry * 0.9, O);
                    var hem = (int)(l.Bottom - 0.5);
                    c.Rect(R(cx - rx), R(cy - ry * 0.1), R(cx + rx) - 1, hem, O);
                    for (var x = 0; x < BuddySpriteGrid.Size; x++)
                    {
                        if ((x + 1) % 3 == 0 && c[x, hem] == O) c[x, hem] = BuddyPixel.Transparent;
                    }

                    return FaceIn(cx, cy - ry * 0.25, rx, ry, Mouth.Smile);
                }

                case BuddySpecies.Cactus:
                {
                    // A tall rounded column with stripes, in a pot.
                    var w = Math.Max(MinFaceRx, rx * 0.8);
                    // One row shorter than the stage size: the pot takes the
                    // bottom, and a halo or a flower still needs room above the head.
                    c.Ellipse(cx, cy + 1, w, ry - 1, O);
                    c.Rect(R(cx - w), (int)cy, R(cx + w) - 1, (int)(l.Bottom - 0.5), O);
                    return FaceIn(cx, cy + 1, w, ry - 1, Mouth.Smile);
                }

                case BuddySpecies.Mushroom:
                {
                    // A stem with the face on it, under a spotted cap.
                    var stemRx = Math.Max(MinFaceRx, rx * 0.62);
                    var stemTop = cy - ry * 0.2;
                    c.Rect(R(cx - stemRx), R(stemTop), R(cx + stemRx) - 1, (int)(l.Bottom - 0.5), BuddyPixel.Belly);
                    c.Ellipse(cx, stemTop, rx * 1.2, ry * 0.62, O, maxY: stemTop + 0.5);
                    var faceCy = (stemTop + l.Bottom) / 2;
                    return FaceIn(cx, faceCy, stemRx, (l.Bottom - stemTop) / 2, Mouth.Smile);
                }

                case BuddySpecies.Robot:
                {
                    // A box with its corners knocked off, a screen for a face,
                    // one antenna and two legs.
                    var x0 = R(cx - rx * 0.95);
                    var x1 = R(cx + rx * 0.95) - 1;
                    var y0 = R(cy - ry * 0.85);
                    var y1 = (int)(l.Bottom - 1.5);
                    c.Rect(x0, y0, x1, y1, O);
                    c[x0, y0] = c[x1, y0] = c[x0, y1] = c[x1, y1] = BuddyPixel.Transparent;
                    Feet(c, l, BuddyPixel.Shade);
                    return FaceIn(cx, (y0 + y1) / 2.0 - 0.5, rx * 0.95, (y1 - y0) / 2.0, Mouth.Screen);
                }

                default: // BuddySpecies.Blob
                {
                    // A drop that has settled: wide at the base, a bump on top,
                    // and a drip or two running off it.
                    c.Ellipse(cx, cy + ry * 0.2, rx * 1.1, ry * 0.8, O);
                    c.Ellipse(cx, cy - ry * 0.35, rx * 0.65, ry * 0.65, O);
                    var bottom = (int)(l.Bottom - 0.5);
                    c[R(cx - rx * 0.6), bottom + 1] = O;
                    c[R(cx + rx * 0.4), bottom + 1] = O;
                    return FaceIn(cx, cy + ry * 0.1, rx, ry * 0.8, Mouth.Smile);
                }
            }
        }

        // Two stubby feet under the body, a hand's width apart.
        private static void Feet(Canvas c, Layout l, BuddyPixel role, bool wide = false)
        {
            // A hatchling still in its eggshell has its feet inside it.
            if (l.Stage == 0 && l.InEgg) return;

            var y = (int)(l.Bottom - 0.5) + 1;
            var dx = R(l.Rx * (wide ? 0.75 : 0.45));
            c.Mirrored(l.Cx, dx, y, role);
            c.Mirrored(l.Cx, dx + 1, y, role);
        }

        private static void Ears(Canvas c, double cx, double top, double rx, bool pointy)
        {
            var dx = R(rx * 0.6);
            var y = R(top);
            if (pointy)
            {
                // A triangle each side, pink inside.
                Mirror(c, cx, y - 2, dx, new[] { "o..", "oo.", "ooo" }, BuddyPixel.Body);
                c.Mirrored(cx, dx, y - 1, BuddyPixel.Accent);
            }
            else
            {
                Mirror(c, cx, y - 1, dx, new[] { "o.", "oo" }, BuddyPixel.Body);
            }
        }

        // Draws `rows` with its left column at cx + dx (right-hand copy) and
        // its mirror image at cx - dx - 1 (left-hand copy), top row at `top`.
        // A '.' leaves the cell alone; anything else is `role`.
        private static void Mirror(Canvas c, double cx, double top, int dx, string[] rows, BuddyPixel role)
        {
            var y0 = R(top);
            for (var r = 0; r < rows.Length; r++)
            {
                for (var i = 0; i < rows[r].Length; i++)
                {
                    if (rows[r][i] == '.') continue;
                    c.Mirrored(cx, dx + rows[r].Length - 1 - i, y0 + r, role);
                }
            }
        }

        // ---- evolution parts -------------------------------------------

        private static void DrawFamilyParts(Canvas c, BuddyGenome g, Layout l, Face face)
        {
            var s = l.Stage;
            if (s < 2) return;

            var big = s == 3;
            var (cx, cy, rx, ry) = (l.Cx, l.Cy, l.Rx, l.Ry);
            var side = (int)Math.Floor(rx);

            switch (g.Family)
            {
                case BuddyFamily.Birds:
                    // Wings folded against the sides, held out further at the
                    // last stage.
                    Mirror(c, cx, cy - 1, side - 1, big ? new[] { "ss.", "sss", "sss", ".ss", "..s" } : new[] { "s.", "ss", "ss", ".s" }, BuddyPixel.Shade);
                    break;

                case BuddyFamily.Critters:
                    // A tail curling up on the right.
                    var tx = (int)Math.Floor(cx + rx) - 1;
                    var ty = R(cy + ry * 0.3);
                    c.Pattern(tx, ty - (big ? 4 : 3), big
                        ? new[] { "..oo", "...o", "..oo", ".oo.", "oo.." }
                        : new[] { "..o", "..o", ".oo", "oo." }, BuddyPixel.Body);
                    if (big)
                    {
                        // Cheek tufts, two spikes of fur each side.
                        Mirror(c, cx, cy + 1, side - 1, new[] { "o.", ".o", "o." }, BuddyPixel.Body);
                    }

                    break;

                case BuddyFamily.Aquatic:
                    // Side fins, swept back.
                    Mirror(c, cx, cy + 1, side - 1, big ? new[] { "a..", "aa.", "aaa", "aa." } : new[] { "a.", "aa", "a." }, BuddyPixel.Accent);
                    break;

                case BuddyFamily.Mythic:
                    // Bat wings from the shoulders.
                    Mirror(c, cx, cy - ry * 0.5, side - 1, big
                        ? new[] { "..ss", ".sss", "ssss", "ss.s", "s..." }
                        : new[] { ".ss", "sss", "s.s" }, BuddyPixel.Shade);
                    break;

                case BuddyFamily.Flora:
                    // A leaf either side, low down.
                    Mirror(c, cx, cy + ry * 0.2, side - 2, big ? new[] { ".ll", "lll", "ll." } : new[] { "ll", "l." }, BuddyPixel.Leaf);
                    break;

                case BuddyFamily.Constructs:
                    // Bolts on the sides, and at the last stage little arms.
                    Mirror(c, cx, face.Cy - 1, side - 1, new[] { "a", "a" }, BuddyPixel.Accent);
                    if (big) Mirror(c, cx, cy + 2, side, new[] { "s.", "ss", ".s" }, BuddyPixel.Shade);
                    break;
            }
        }

        // What the last evolution wears on its head — drawn only when there is
        // no hat, so a hat always wins the top of the head outright.
        private static void DrawCrown(Canvas c, BuddyGenome g, Layout l, int headTop)
        {
            var s = l.Stage;
            var cx = l.Cx;

            // The robot's antenna is the species', not the stage's, so it is
            // here at every stage — still only without a hat.
            if (g.Species == BuddySpecies.Robot)
            {
                c[11, headTop - 1] = c[11, headTop - 2] = BuddyPixel.Shade;
                c[11, headTop - 3] = c[12, headTop - 3] = BuddyPixel.Accent;
            }

            if (g.Family == BuddyFamily.Constructs && s == 0 && g.Species != BuddySpecies.Robot)
            {
                c[12, headTop - 1] = BuddyPixel.Shade;
                c[12, headTop - 2] = BuddyPixel.Accent;
            }

            if (s < 3) return;

            switch (g.Family)
            {
                case BuddyFamily.Birds:
                    c.Pattern(10, headTop - 3, new[] { ".a.a", "a.a.", ".aa." }, BuddyPixel.Accent);
                    break;
                case BuddyFamily.Critters:
                    // A tuft of fur.
                    c.Pattern(11, headTop - 2, new[] { "o.", ".o" }, BuddyPixel.Body);
                    c.Pattern(12, headTop - 2, new[] { ".o", "o." }, BuddyPixel.Body);
                    break;
                case BuddyFamily.Aquatic:
                    c.Pattern(10, headTop - 2, new[] { ".aa.", "aaaa" }, BuddyPixel.Accent);
                    // Two bubbles drifting up beside it.
                    c[3, 5] = c[5, 2] = BuddyPixel.EyeShine;
                    break;
                case BuddyFamily.Mythic:
                    Mirror(c, cx, headTop - 2, 3, new[] { ".a", "a." }, BuddyPixel.Accent);
                    break;
                case BuddyFamily.Flora:
                    c.Pattern(10, headTop - 3, new[] { ".ff.", "fbbf", ".ff.", "..l." }, BuddyPixel.Bloom);
                    break;
                case BuddyFamily.Constructs:
                    if (g.Species != BuddySpecies.Robot)
                    {
                        c[12, headTop - 1] = c[12, headTop - 2] = BuddyPixel.Shade;
                        c[12, headTop - 3] = BuddyPixel.Accent;
                    }

                    break;
            }
        }

        // ---- markings, props -------------------------------------------

        private static void DrawMarkings(Canvas c, BuddySpecies species, Layout l, Face face)
        {
            var (cx, cy, rx, ry) = (l.Cx, l.Cy, l.Rx, l.Ry);
            const BuddyPixel B = BuddyPixel.Belly;

            switch (species)
            {
                case BuddySpecies.Duck:
                case BuddySpecies.Chonk:
                case BuddySpecies.Cat:
                case BuddySpecies.Dragon:
                case BuddySpecies.Rabbit:
                    c.Ellipse(cx, cy + ry * 0.55, rx * 0.55, ry * 0.45, B, onlyOn: BuddyPixel.Body);
                    if (species == BuddySpecies.Chonk)
                    {
                        // Tabby stripes on the forehead.
                        var top = c.TopOfColumns(11, 12);
                        c[10, top + 1] = c[12, top + 1] = c[14, top + 1] = BuddyPixel.Shade;
                        c[10, top + 2] = c[12, top + 2] = c[14, top + 2] = BuddyPixel.Shade;
                    }

                    if (species == BuddySpecies.Dragon)
                    {
                        // Scales across the belly.
                        for (var y = (int)(cy + ry * 0.35); y <= (int)(cy + ry * 0.9); y += 2)
                        {
                            for (var x = 0; x < BuddySpriteGrid.Size; x++)
                            {
                                if (c[x, y] == B && x % 2 == 0) c[x, y] = BuddyPixel.Accent;
                            }
                        }
                    }

                    break;

                case BuddySpecies.Goose:
                case BuddySpecies.Axolotl:
                case BuddySpecies.Capybara:
                case BuddySpecies.Blob:
                    c.Ellipse(cx, l.Bottom - ry * 0.35, rx * 0.55, ry * 0.35, B, onlyOn: BuddyPixel.Body);
                    if (species == BuddySpecies.Blob)
                    {
                        // The shine that makes a blob wet.
                        var top = c.TopOfColumns(11, 12);
                        c[9, top + 1] = c[8, top + 2] = BuddyPixel.EyeShine;
                    }

                    break;

                case BuddySpecies.Owl:
                    // A pale face disc, and chevrons down the chest.
                    c.Ellipse(face.Cx, face.Cy + 0.5, face.Rx * 0.95, face.Ry * 0.55, B, onlyOn: BuddyPixel.Body);
                    for (var y = (int)(cy + ry * 0.55); y < (int)(cy + ry); y += 2)
                    {
                        c[10, y] = c[13, y] = BuddyPixel.Shade;
                        c[11, y + 1] = c[12, y + 1] = BuddyPixel.Shade;
                    }

                    break;

                case BuddySpecies.Penguin:
                    // White face and front, a dinner-jacket penguin.
                    c.Ellipse(cx, cy + ry * 0.25, rx * 0.68, ry * 0.8, B, onlyOn: BuddyPixel.Body);
                    c.Ellipse(cx, face.Cy + 0.5, rx * 0.72, ry * 0.4, B, onlyOn: BuddyPixel.Body);
                    break;

                case BuddySpecies.Octopus:
                    // Spots on the dome.
                    var dome = c.TopOfColumns(11, 12);
                    c[9, dome + 1] = c[14, dome + 2] = c[11, dome + 1] = BuddyPixel.Shade;
                    break;

                case BuddySpecies.Turtle:
                    // Scutes: a lattice of darker plates on the shell.
                    for (var y = 0; y < BuddySpriteGrid.Size; y++)
                    {
                        for (var x = 0; x < BuddySpriteGrid.Size; x++)
                        {
                            if (c[x, y] == BuddyPixel.Accent && y % 2 == 1 && (x + y / 2 * 2) % 4 == 0) c[x, y] = BuddyPixel.PropShade;
                        }
                    }

                    c.Ellipse(face.Cx, face.Cy + face.Ry * 0.6, face.Rx * 0.5, face.Ry * 0.35, B, onlyOn: BuddyPixel.Body);
                    break;

                case BuddySpecies.Snail:
                {
                    // A spiral on the shell, and the two stalks.
                    var shellTop = c.TopOfColumns(11, 12);
                    var sx = 12;
                    var sy = shellTop + 2;
                    c.Pattern(sx - 2, sy, new[] { ".RRR", "R..R", "R.R.", "R..." }, BuddyPixel.PropShade);
                    var headTop = R(face.Cy - face.Ry);
                    c[9, headTop - 1] = c[14, headTop - 1] = BuddyPixel.Body;
                    c[9, headTop - 2] = c[14, headTop - 2] = BuddyPixel.Belly;
                    break;
                }

                case BuddySpecies.Ghost:
                    // Two little arms out to the sides.
                    Mirror(c, cx, cy + ry * 0.2, R(rx), new[] { "o" }, BuddyPixel.Body);
                    break;

                case BuddySpecies.Cactus:
                    // Ribs and spines.
                    for (var y = 0; y < BuddySpriteGrid.Size; y++)
                    {
                        if (c[10, y] == BuddyPixel.Body) c[10, y] = BuddyPixel.Shade;
                        if (c[13, y] == BuddyPixel.Body) c[13, y] = BuddyPixel.Shade;
                    }

                    var ctop = c.TopOfColumns(11, 12);
                    c.Mirrored(cx, R(face.Rx), ctop + 3, BuddyPixel.Accent);
                    c.Mirrored(cx, R(face.Rx), ctop + 6, BuddyPixel.Accent);
                    break;

                case BuddySpecies.Mushroom:
                {
                    // White spots on the cap.
                    var top = c.TopOfColumns(11, 12);
                    c[11, top + 1] = c[12, top + 1] = BuddyPixel.Accent;
                    c.Mirrored(cx, R(rx * 0.7), top + 2, BuddyPixel.Accent);
                    c.Mirrored(cx, R(rx * 0.7) - 1, top + 2, BuddyPixel.Accent);
                    // The stem is Belly, which ShadeBody leaves alone; its
                    // right edge gets shaded here instead.
                    for (var y = 0; y < BuddySpriteGrid.Size; y++)
                    {
                        for (var x = BuddySpriteGrid.Size - 2; x >= 12; x--)
                        {
                            if (c[x, y] == B && c[x + 1, y] == BuddyPixel.Transparent) { c[x, y] = BuddyPixel.PropShade; break; }
                        }
                    }

                    break;
                }

                case BuddySpecies.Robot:
                {
                    // The screen the face is drawn on.
                    var x0 = R(face.Cx - face.Rx) + 1;
                    var x1 = R(face.Cx + face.Rx) - 2;
                    var y0 = R(face.Cy - face.Ry * 0.55);
                    var y1 = R(face.Cy + face.Ry * 0.65);
                    c.Rect(x0, y0, x1, y1, B);
                    break;
                }
            }
        }

        private static void DrawProp(Canvas c, BuddyGenome g, Layout l)
        {
            var cx = l.Cx;
            if (g.Species == BuddySpecies.Cactus || (g.Family == BuddyFamily.Flora && l.Stage == 0))
            {
                Flowerpot(c, (int)(l.Bottom + 0.5), l.Stage == 0 ? 5 : 6);
                return;
            }

            if (l.Stage != 0) return;

            switch (g.Family)
            {
                case BuddyFamily.Birds:
                case BuddyFamily.Mythic:
                {
                    // The bottom half of the egg it came out of, with a
                    // zig-zag crack for a rim. The mythic egg is speckled.
                    var top = 19;
                    for (var y = top; y <= 22; y++)
                    {
                        for (var x = 0; x < BuddySpriteGrid.Size; x++)
                        {
                            var inside = Math.Pow((x + 0.5 - cx) / 6.2, 2) + Math.Pow((y + 0.5 - 18.5) / 4.6, 2) <= 1;
                            if (!inside) continue;
                            if (y == top && x % 2 == 0) continue;
                            var speck = g.Family == BuddyFamily.Mythic && (x * 3 + y * 5) % 7 == 0;
                            c[x, y] = speck || x >= 16 ? BuddyPixel.PropShade : BuddyPixel.Prop;
                        }
                    }

                    break;
                }

                case BuddyFamily.Aquatic:
                    Puddle(c);
                    break;
            }
        }

        // A flowerpot: a rim `2 * halfWidth` wide on row `rim`, and a base
        // tapering down to row 22.
        private static void Flowerpot(Canvas c, int rim, int halfWidth)
        {
            c.Rect(12 - halfWidth, rim, 12 + halfWidth - 1, rim, BuddyPixel.PropShade);
            for (var y = rim + 1; y <= 22; y++)
            {
                var inset = (y - rim + 1) / 2;
                c.Rect(12 - halfWidth + inset, y, 12 + halfWidth - 1 - inset, y, BuddyPixel.Prop);
            }
        }

        // A puddle to sit in, a ripple wider than the body.
        private static void Puddle(Canvas c)
        {
            c.Rect(5, 21, 18, 21, BuddyPixel.Prop);
            c.Rect(7, 22, 16, 22, BuddyPixel.PropShade);
            c[4, 20] = c[19, 20] = BuddyPixel.Prop;
        }

        // ---- the egg ----------------------------------------------------

        // Every buddy starts here, and every reborn one comes back here. An
        // egg is an oval a little narrower at the top than at the bottom —
        // at this size that taper is the whole difference between an egg and
        // a ball — lit from the upper left like everything else, with the
        // family's flavour on the shell rather than detail a 24x24 egg cannot
        // hold. No face and no hat: those are the buddy's, and it has not
        // come out yet.
        //
        // It rocks instead of bobbing: for one quarter of the loop its top
        // leans a pixel left, and for another a pixel right. The bottom stays
        // put, so an egg in a pot or on a puddle tips in it rather than
        // sliding across it.
        private const double EggRx = 5.0, EggRy = 6.5;

        private static void DrawEgg(Canvas c, BuddyGenome g, int frame)
        {
            var lean = (frame / 4) switch { 1 => -1, 3 => 1, _ => 0 };
            var family = g.Family;
            // A flora egg sits low enough that its pot, drawn over it, hides
            // its bottom — planted rather than balanced on the rim.
            var bottom = family switch
            {
                BuddyFamily.Aquatic => 20.5,
                BuddyFamily.Flora => 22.5,
                _ => 21.5,
            };
            var cy = bottom - EggRy;

            // Birds, mythic and aquatic eggs are the family's shell colour —
            // the same one the hatchling's eggshell or puddle is later drawn
            // in; the rest wear the species' own.
            // A flora seed is the paler belly colour, spotted in the body's:
            // the other way round, a red egg with pale spots and a sprout on
            // top was a strawberry.
            var propShell = family is BuddyFamily.Birds or BuddyFamily.Mythic or BuddyFamily.Aquatic;
            var shell = propShell ? BuddyPixel.Prop : family == BuddyFamily.Flora ? BuddyPixel.Belly : BuddyPixel.Body;
            var shade = propShell || family == BuddyFamily.Flora ? BuddyPixel.PropShade : BuddyPixel.Shade;

            // Marks are placed in the egg's own coordinates, before the lean,
            // so a speckle rocks with the shell it is on.
            var top = -1;
            for (var y = 0; y < BuddySpriteGrid.Size; y++)
            {
                var shift = Leans(y, cy) ? lean : 0;
                for (var lx = 0; lx < BuddySpriteGrid.Size; lx++)
                {
                    if (!InEgg(lx, y, 12, cy)) continue;
                    if (top < 0) top = y;
                    // Shaded where the egg, moved up and left, no longer
                    // covers it: a crescent along the lower right.
                    var role = InEgg(lx, y, 10.8, cy - 1.2) ? shell : shade;
                    role = Mark(g, lx, y, cy, role, shell) ?? role;
                    c[lx + shift, y] = role;
                }
            }

            // The shine that makes a shell look hard, upper left.
            Local(c, lean, cy, 9, R(cy) - 4, BuddyPixel.EyeShine);
            Local(c, lean, cy, 9, R(cy) - 3, BuddyPixel.EyeShine);
            Local(c, lean, cy, 10, R(cy) - 5, BuddyPixel.EyeShine);

            switch (family)
            {
                case BuddyFamily.Aquatic:
                    // The little one, curled up inside the jelly.
                    c.Ellipse(12, cy + 1.5, 2.3, 2.1, BuddyPixel.Body, onlyOn: shell);
                    c.Ellipse(12, cy + 1.5, 2.3, 2.1, BuddyPixel.Body, onlyOn: shade);
                    c[13, R(cy) + 2] = c[12, R(cy) + 3] = c[13, R(cy) + 3] = BuddyPixel.Shade;
                    Puddle(c);
                    break;

                case BuddyFamily.Flora:
                    // A sprout from the top, and the pot the hatchling will
                    // still be sitting in.
                    c[12 + lean, top - 1] = c[12 + lean, top - 2] = BuddyPixel.Leaf;
                    c[13 + lean, top - 3] = c[14 + lean, top - 3] = c[11 + lean, top - 2] = BuddyPixel.Leaf;
                    Flowerpot(c, 21, 5);
                    break;

                case BuddyFamily.Constructs:
                {
                    // A seam around the middle with a rivet at each end, and
                    // a porthole above it with the core glowing through.
                    var seam = R(cy) + 1;
                    int left = -1, right = -1;
                    for (var x = 0; x < BuddySpriteGrid.Size; x++)
                    {
                        if (c[x, seam] == BuddyPixel.Transparent) continue;
                        if (left < 0) left = x;
                        right = x;
                        c[x, seam] = BuddyPixel.Shade;
                    }

                    c[left + 1, seam] = c[right - 1, seam] = BuddyPixel.Accent;
                    for (var y = seam - 3; y <= seam - 2; y++)
                    {
                        Local(c, lean, cy, 11, y, BuddyPixel.Bloom);
                        Local(c, lean, cy, 12, y, BuddyPixel.Bloom);
                    }

                    break;
                }
            }
        }

        // Inside the egg centred at (cx, cy), for cell (x, y): an ellipse
        // whose half-width grows from the top down, which is the taper.
        private static bool InEgg(double x, double y, double cx, double cy)
        {
            var t = (y + 0.5 - cy) / EggRy;
            var w = EggRx * (1 + 0.18 * t);
            var dx = (x + 0.5 - cx) / w;
            return dx * dx + t * t <= 1;
        }

        // A cell given in the egg's own coordinates, moved by the lean if it
        // is in the leaning half.
        private static void Local(Canvas c, int lean, double cy, int x, int y, BuddyPixel role) =>
            c[x + (Leans(y, cy) ? lean : 0), y] = role;

        // Only the narrow top third tips. Tipping the whole top half put the
        // one-pixel step at the egg's widest rows, where it read as a notch
        // cut out of the shell rather than as the egg rocking.
        private static bool Leans(int y, double cy) => y < cy - 3;

        // A family's marks on the shell, in the egg's own coordinates; null
        // leaves the cell as it was. Only ever on the lit shell, so a mark
        // never breaks up the shading crescent.
        private static BuddyPixel? Mark(BuddyGenome g, int x, int y, double cy, BuddyPixel role, BuddyPixel shell)
        {
            if (role != shell) return null;
            var dx = x - 12;
            var dy = y - R(cy);
            switch (g.Family)
            {
                case BuddyFamily.Birds:
                    // A few freckles, placed by hand: a pattern formula at
                    // this density comes out in rows.
                    return (dx, dy) is (-3, -1) or (1, -3) or (2, 0) or (-1, 2) or (-3, 3) or (3, 3) or (0, -5)
                        ? BuddyPixel.Accent
                        : null;

                case BuddyFamily.Mythic:
                    // The hatchling's shell uses the same speckle, so the egg
                    // it sits in later is recognisably this one.
                    return (x * 3 + y * 5) % 7 == 0 ? BuddyPixel.PropShade : null;

                case BuddyFamily.Critters:
                {
                    // Two soft patches in the belly colour, calico fashion,
                    // clear of the shine on the upper left. Drawn by hand with
                    // their corners knocked off: an ellipse this small comes
                    // out a square.
                    var a = dy switch { -4 => dx is 1 or 2, -3 => dx is >= 0 and <= 3, -2 => dx is 1 or 2, _ => false };
                    var b = dy switch { 1 => dx is -3 or -2, 2 => dx is >= -4 and <= -1, 3 => dx is -3 or -2, _ => false };
                    return a || b ? BuddyPixel.Belly : null;
                }

                case BuddyFamily.Flora:
                    // Spots in the species' body colour: a mushroom's red, a
                    // cactus's green.
                    return (dx, dy) is (-2, -2) or (1, -4) or (2, 0) or (-2, 3) or (1, 3) ? BuddyPixel.Body : null;

                default:
                    return null;
            }
        }

        // ---- hats -------------------------------------------------------

        // Hats are drawn from the top of the head, whose row is found rather
        // than computed, so one hat sits on every species. The bottom row of
        // each pattern lands one row into the head, so a hat is worn rather
        // than hovering — except the halo, which is meant to hover.
        //
        // 'h' Hat, 'H' HatShade, 'g' HatAccent, 'k' a dark pixel (the tiny
        // duck's eye), '.' nothing.
        private static readonly Dictionary<BuddyHat, string[]> Hats = new()
        {
            [BuddyHat.Crown] = new[]
            {
                "g..gg..g",
                "h.hhhh.h",
                "hhhhhhhh",
                "hghhhhgh",
                "HHHHHHHH",
            },
            [BuddyHat.TopHat] = new[]
            {
                "..hhhhhh..",
                "..hhhhhh..",
                "..hhhhhh..",
                "..gggggg..",
                "HHHHHHHHHH",
            },
            [BuddyHat.Propeller] = new[]
            {
                "gggHHhhh",
                "...HH...",
                "..hhgg..",
                ".hhhggg.",
                "hhhhgggg",
            },
            [BuddyHat.Halo] = new[]
            {
                ".hhhhhh.",
                "hH....Hh",
                ".hhhhhh.",
                "........",
                "........",
                "........",
            },
            [BuddyHat.Wizard] = new[]
            {
                "......h...",
                ".....hh...",
                "....hhgh..",
                "...hhhhhh.",
                "..hhhhhhh.",
                "HHHHHHHHHH",
            },
            [BuddyHat.Beanie] = new[]
            {
                "...gg...",
                "..hhhh..",
                ".hhhhhh.",
                "hhhhhhhh",
                "HHHHHHHH",
            },
            [BuddyHat.TinyDuck] = new[]
            {
                "...hh.",
                "..hkhg",
                "h.hhh.",
                "hhhhh.",
                ".HHH..",
            },
        };

        private static void DrawHat(Canvas c, BuddyHat hat, int headTop)
        {
            var rows = Hats[hat];
            var width = rows[0].Length;
            var x0 = 12 - width / 2;
            var y0 = headTop + 1 - (rows.Length - 1);
            for (var r = 0; r < rows.Length; r++)
            {
                for (var i = 0; i < width; i++)
                {
                    var role = rows[r][i] switch
                    {
                        'h' => BuddyPixel.Hat,
                        'H' => BuddyPixel.HatShade,
                        'g' => BuddyPixel.HatAccent,
                        'k' => BuddyPixel.Outline,
                        _ => BuddyPixel.Transparent,
                    };
                    if (role != BuddyPixel.Transparent) c[x0 + i, y0 + r] = role;
                }
            }
        }

        // ---- face -------------------------------------------------------

        // Each eye is drawn into a 3x3 box; these are the left eye, and the
        // right eye is its mirror image. 'e' Eye, 'w' EyeShine, '.' leaves
        // whatever is underneath. The small styles hug the inner edge of the
        // box, so a pair of dots sits close together the way a pet's do.
        private static readonly Dictionary<BuddyEyes, string[]> EyeStyles = new()
        {
            [BuddyEyes.Dot] = new[] { ".we", ".ee", "..." },
            [BuddyEyes.Star] = new[] { ".e.", "eee", ".e." },
            [BuddyEyes.Cross] = new[] { "e.e", ".e.", "e.e" },
            [BuddyEyes.Ring] = new[] { "eee", "ewe", "eee" },
            [BuddyEyes.Spiral] = new[] { "eee", "e.e", "ee." },
            [BuddyEyes.Degree] = new[] { ".e.", "e.e", ".e." },
        };

        // The row a blink collapses each style to: the pattern's own width,
        // on its middle row, so a closed eye is exactly as wide as the open
        // one was.
        private static void DrawFace(Canvas c, BuddyGenome g, Face face, bool blink)
        {
            var gap = face.Rx >= 6.5 ? 4 : 2;
            var leftX = 12 - gap / 2 - 3;
            var rightX = 12 + gap / 2;
            var top = R(face.Cy - face.Ry * 0.2) - 1;

            var rows = EyeStyles[g.Eyes];
            for (var r = 0; r < 3; r++)
            {
                for (var i = 0; i < 3; i++)
                {
                    var ch = rows[r][i];
                    if (blink)
                    {
                        var column = rows[0][i] != '.' || rows[1][i] != '.' || rows[2][i] != '.';
                        ch = r == 1 && column ? 'e' : '.';
                    }

                    if (ch == '.') continue;
                    var role = ch == 'w' ? BuddyPixel.EyeShine : BuddyPixel.Eye;
                    c[leftX + i, top + r] = role;
                    c[rightX + 2 - i, top + r] = role;
                }
            }

            var m = top + 3;

            // Cheeks, just outside and below each eye — only on skin, so a
            // cheek never lands on an outline or off the edge of a face.
            foreach (var x in new[] { leftX - 1, leftX, rightX + 2, rightX + 3 })
            {
                if (c[x, m] == BuddyPixel.Body || c[x, m] == BuddyPixel.Shade) c[x, m] = BuddyPixel.Blush;
            }

            const BuddyPixel A = BuddyPixel.Accent;
            const BuddyPixel K = BuddyPixel.Outline;
            switch (face.Mouth)
            {
                case Mouth.Bill:
                    c.Rect(10, m, 13, m, A);
                    c.Rect(11, m + 1, 12, m + 1, A);
                    break;
                case Mouth.Beak:
                    c[11, m] = c[12, m] = A;
                    c[11, m + 1] = A;
                    break;
                case Mouth.Snout:
                    c.Rect(10, m, 13, m + 1, BuddyPixel.Belly);
                    c[10, m] = c[13, m] = K;
                    break;
                case Mouth.Cat:
                    c[10, m] = c[13, m] = K;
                    c[11, m + 1] = c[12, m + 1] = K;
                    c[11, m] = c[12, m] = BuddyPixel.Blush;
                    break;
                case Mouth.Screen:
                case Mouth.Smile:
                    if (gap == 2)
                    {
                        c[11, m] = c[12, m] = K;
                    }
                    else
                    {
                        c[10, m] = c[13, m] = K;
                        c[11, m + 1] = c[12, m + 1] = K;
                    }

                    break;
            }
        }

        // ---- shiny ------------------------------------------------------

        // Four sparkle sites in the corners the body leaves free; two light
        // up at a time and swap every other frame, so a shiny twinkles. They
        // are drawn after the bob and do not move with it — sparkles in the
        // air, not stuck to the buddy.
        private static readonly (int X, int Y)[] SparkleSites = { (3, 3), (20, 6), (2, 15), (21, 18) };

        private static void DrawSparkles(Canvas c, int frame)
        {
            var phase = frame / 2 % 2;
            for (var i = 0; i < SparkleSites.Length; i++)
            {
                var (x, y) = SparkleSites[i];
                var big = i % 2 == phase;
                c.Soft(x, y, BuddyPixel.Sparkle);
                if (!big) continue;
                c.Soft(x - 1, y, BuddyPixel.Sparkle);
                c.Soft(x + 1, y, BuddyPixel.Sparkle);
                c.Soft(x, y - 1, BuddyPixel.Sparkle);
                c.Soft(x, y + 1, BuddyPixel.Sparkle);
            }
        }

        // ---- palette ----------------------------------------------------

        // ARGB per role, indexed by (int)BuddyPixel. Kept here rather than in
        // the control so it stays pure and a test can ask whether shiny or
        // rarity actually changed a colour.
        //
        // Rarity is saturation: a common buddy is a little muted and a
        // legendary one is as vivid as its species gets. Shiny swings the hue
        // to the opposite side of the wheel — and for the near-white species,
        // where a hue swing changes nothing a person can see, lifts the
        // saturation too so they come out tinted.
        internal static uint[] Palette(BuddyGenome genome)
        {
            var spec = Colours(genome.Species);
            var satScale = genome.Rarity switch
            {
                BuddyRarity.Common => 0.62,
                BuddyRarity.Uncommon => 0.76,
                BuddyRarity.Rare => 0.88,
                BuddyRarity.Epic => 1.0,
                _ => 1.15,
            };

            Hsl Adjust(Hsl c)
            {
                var h = c.H;
                var s = Math.Min(1, c.S * satScale);
                if (genome.Shiny)
                {
                    h = (h + 180) % 360;
                    s = Math.Max(s, 0.5);
                }

                return c with { H = h, S = s };
            }

            var body = Adjust(spec.Body);
            var belly = Adjust(spec.Belly);
            var accent = Adjust(spec.Accent);
            var (hat, hatShade, hatAccent) = HatColours(genome.Hat);
            var (prop, propShade) = PropColours(genome);

            var p = new uint[(int)BuddyPixel.Bloom + 1];
            p[(int)BuddyPixel.Transparent] = 0x00000000;
            p[(int)BuddyPixel.Outline] = new Hsl(body.H, 0.35, 0.13).ToArgb();
            p[(int)BuddyPixel.Body] = body.ToArgb();
            p[(int)BuddyPixel.Shade] = (body with { L = Math.Max(0.12, body.L - 0.14), S = Math.Min(1, body.S + 0.05) }).ToArgb();
            p[(int)BuddyPixel.Belly] = belly.ToArgb();
            p[(int)BuddyPixel.Eye] = new Hsl(250, 0.3, 0.12).ToArgb();
            p[(int)BuddyPixel.Accent] = accent.ToArgb();
            p[(int)BuddyPixel.Hat] = hat.ToArgb();
            p[(int)BuddyPixel.HatShade] = hatShade.ToArgb();
            p[(int)BuddyPixel.Sparkle] = new Hsl(50, 1, 0.82).ToArgb();
            p[(int)BuddyPixel.EyeShine] = 0xFFFFFFFF;
            p[(int)BuddyPixel.Blush] = new Hsl(350, 0.85, 0.74).ToArgb();
            p[(int)BuddyPixel.HatAccent] = hatAccent.ToArgb();
            p[(int)BuddyPixel.Prop] = prop.ToArgb();
            p[(int)BuddyPixel.PropShade] = propShade.ToArgb();
            p[(int)BuddyPixel.Leaf] = new Hsl(110, 0.55, 0.42).ToArgb();
            p[(int)BuddyPixel.Bloom] = (genome.Family == BuddyFamily.Constructs ? new Hsl(185, 0.9, 0.62) : new Hsl(330, 0.8, 0.72)).ToArgb();
            return p;
        }

        private readonly record struct SpeciesColours(Hsl Body, Hsl Belly, Hsl Accent);

        private static SpeciesColours Colours(BuddySpecies species) => species switch
        {
            BuddySpecies.Duck => new(new(50, 0.9, 0.62), new(52, 0.9, 0.82), new(28, 0.95, 0.55)),
            BuddySpecies.Goose => new(new(40, 0.18, 0.92), new(40, 0.1, 0.98), new(25, 0.95, 0.55)),
            BuddySpecies.Owl => new(new(28, 0.45, 0.44), new(35, 0.5, 0.76), new(40, 0.9, 0.55)),
            BuddySpecies.Penguin => new(new(220, 0.35, 0.3), new(210, 0.15, 0.95), new(35, 0.95, 0.55)),
            BuddySpecies.Cat => new(new(25, 0.85, 0.6), new(30, 0.7, 0.86), new(345, 0.75, 0.78)),
            BuddySpecies.Rabbit => new(new(30, 0.2, 0.9), new(0, 0, 1), new(345, 0.8, 0.8)),
            BuddySpecies.Capybara => new(new(25, 0.42, 0.5), new(25, 0.35, 0.68), new(20, 0.35, 0.35)),
            BuddySpecies.Chonk => new(new(35, 0.28, 0.62), new(35, 0.25, 0.88), new(345, 0.65, 0.78)),
            BuddySpecies.Octopus => new(new(350, 0.7, 0.64), new(350, 0.6, 0.82), new(350, 0.55, 0.48)),
            BuddySpecies.Axolotl => new(new(330, 0.7, 0.8), new(330, 0.55, 0.9), new(340, 0.8, 0.6)),
            BuddySpecies.Turtle => new(new(95, 0.5, 0.58), new(60, 0.55, 0.8), new(30, 0.5, 0.42)),
            BuddySpecies.Snail => new(new(40, 0.4, 0.72), new(45, 0.5, 0.86), new(20, 0.65, 0.52)),
            BuddySpecies.Dragon => new(new(150, 0.6, 0.45), new(55, 0.75, 0.75), new(40, 0.4, 0.86)),
            BuddySpecies.Ghost => new(new(250, 0.4, 0.9), new(250, 0.35, 0.97), new(280, 0.45, 0.75)),
            BuddySpecies.Cactus => new(new(125, 0.5, 0.45), new(125, 0.4, 0.62), new(60, 0.7, 0.9)),
            BuddySpecies.Mushroom => new(new(5, 0.8, 0.55), new(40, 0.5, 0.9), new(0, 0, 1)),
            BuddySpecies.Robot => new(new(205, 0.22, 0.68), new(185, 0.55, 0.82), new(45, 0.95, 0.55)),
            _ => new(new(165, 0.65, 0.58), new(165, 0.55, 0.76), new(165, 0.6, 0.4)), // Blob
        };

        private static (Hsl Hat, Hsl Shade, Hsl Accent) HatColours(BuddyHat hat) => hat switch
        {
            BuddyHat.Crown => (new(46, 0.95, 0.56), new(38, 0.9, 0.4), new(350, 0.85, 0.55)),
            BuddyHat.TopHat => (new(240, 0.12, 0.2), new(240, 0.12, 0.12), new(350, 0.75, 0.5)),
            BuddyHat.Propeller => (new(0, 0.8, 0.58), new(220, 0.08, 0.55), new(210, 0.85, 0.58)),
            BuddyHat.Halo => (new(52, 1, 0.72), new(46, 1, 0.6), new(52, 1, 0.9)),
            BuddyHat.Wizard => (new(265, 0.6, 0.45), new(265, 0.6, 0.3), new(50, 1, 0.65)),
            BuddyHat.Beanie => (new(175, 0.6, 0.45), new(175, 0.6, 0.32), new(0, 0, 0.97)),
            BuddyHat.TinyDuck => (new(52, 0.95, 0.62), new(28, 0.95, 0.55), new(28, 0.95, 0.55)),
            _ => (new(0, 0, 0.5), new(0, 0, 0.4), new(0, 0, 0.6)), // None: never drawn
        };

        private static (Hsl Prop, Hsl Shade) PropColours(BuddyGenome g)
        {
            if (g.Family == BuddyFamily.Flora || g.Species == BuddySpecies.Cactus)
                return (new(18, 0.6, 0.52), new(15, 0.55, 0.38));

            return g.Family switch
            {
                BuddyFamily.Birds => (new(45, 0.4, 0.93), new(40, 0.25, 0.78)),
                BuddyFamily.Mythic => (new(100, 0.3, 0.86), new(130, 0.45, 0.5)),
                BuddyFamily.Aquatic => (new(200, 0.75, 0.66), new(205, 0.7, 0.52)),
                // Critters and constructs have no prop; the turtle's scutes use
                // PropShade, which for them is just a darker shell.
                _ => (new(30, 0.4, 0.5), new(28, 0.45, 0.32)),
            };
        }

        private readonly record struct Hsl(double H, double S, double L)
        {
            internal uint ToArgb()
            {
                var c = (1 - Math.Abs(2 * L - 1)) * S;
                var hp = H / 60.0;
                var x = c * (1 - Math.Abs(hp % 2 - 1));
                var (r, g, b) = hp switch
                {
                    < 1 => (c, x, 0.0),
                    < 2 => (x, c, 0.0),
                    < 3 => (0.0, c, x),
                    < 4 => (0.0, x, c),
                    < 5 => (x, 0.0, c),
                    _ => (c, 0.0, x),
                };
                var m = L - c / 2;
                static uint Byte(double v) => (uint)Math.Clamp(Math.Round(v * 255), 0, 255);
                return 0xFF000000 | Byte(r + m) << 16 | Byte(g + m) << 8 | Byte(b + m);
            }
        }

        // ---- the drawing surface ---------------------------------------

        private sealed class Canvas
        {
            private const int N = BuddySpriteGrid.Size;

            internal readonly BuddyPixel[] Cells = new BuddyPixel[N * N];

            // Solid pixels asked for outside the grid. The whole point of
            // counting instead of silently dropping them is the test that
            // asserts it stays zero.
            internal int Clipped;

            private static bool In(int x, int y) => x >= 0 && y >= 0 && x < N && y < N;

            internal BuddyPixel this[int x, int y]
            {
                get => In(x, y) ? Cells[y * N + x] : BuddyPixel.Transparent;
                set
                {
                    if (In(x, y)) Cells[y * N + x] = value;
                    else if (value != BuddyPixel.Transparent) Clipped++;
                }
            }

            // A pixel that may fall off the edge without it counting as a
            // clip, and never covers anything solid — for sparkles, which are
            // decoration in the empty air around a buddy.
            internal void Soft(int x, int y, BuddyPixel role)
            {
                if (In(x, y) && this[x, y] == BuddyPixel.Transparent) this[x, y] = role;
            }

            // Cell (x, y) is inside when its centre is.
            internal void Ellipse(double cx, double cy, double rx, double ry, BuddyPixel role,
                                  double maxY = double.MaxValue, BuddyPixel? onlyOn = null)
            {
                for (var y = 0; y < N; y++)
                {
                    if (y + 0.5 > maxY) continue;
                    for (var x = 0; x < N; x++)
                    {
                        var dx = (x + 0.5 - cx) / rx;
                        var dy = (y + 0.5 - cy) / ry;
                        if (dx * dx + dy * dy > 1) continue;
                        if (onlyOn is { } only && this[x, y] != only) continue;
                        this[x, y] = role;
                    }
                }
            }

            internal void Rect(int x0, int y0, int x1, int y1, BuddyPixel role)
            {
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++) this[x, y] = role;
                }
            }

            // Sets the pair of cells `dx` either side of the centre line
            // x = cx: (cx + dx) and its mirror (cx - dx - 1).
            internal void Mirrored(double cx, int dx, int y, BuddyPixel role)
            {
                var centre = R(cx);
                this[centre + dx, y] = role;
                this[centre - dx - 1, y] = role;
            }

            internal void Pattern(int x0, int y0, string[] rows, BuddyPixel role)
            {
                for (var r = 0; r < rows.Length; r++)
                {
                    for (var i = 0; i < rows[r].Length; i++)
                    {
                        var ch = rows[r][i];
                        if (ch == '.') continue;
                        this[x0 + i, y0 + r] = ch switch
                        {
                            'b' => BuddyPixel.Belly,
                            'l' => BuddyPixel.Leaf,
                            'R' => BuddyPixel.PropShade,
                            _ => role,
                        };
                    }
                }
            }

            // The highest solid row in either of the given columns: the top of
            // the head, measured on the centre line.
            internal int TopOfColumns(int a, int b)
            {
                for (var y = 0; y < N; y++)
                {
                    if (this[a, y] != BuddyPixel.Transparent || this[b, y] != BuddyPixel.Transparent) return y;
                }

                return N / 2;
            }

            // Light from the upper left: a body pixel with open air (or
            // anything that is not body) down and to its right is on the
            // shadowed rim.
            internal void ShadeBody()
            {
                var shade = new List<int>();
                for (var y = 0; y < N; y++)
                {
                    for (var x = 0; x < N; x++)
                    {
                        if (this[x, y] != BuddyPixel.Body) continue;
                        var below = this[x, y + 1];
                        var diagonal = this[x + 1, y + 1];
                        if (below == BuddyPixel.Transparent || diagonal == BuddyPixel.Transparent) shade.Add(y * N + x);
                    }
                }

                foreach (var i in shade) Cells[i] = BuddyPixel.Shade;
            }

            // Every empty cell touching something solid along an edge becomes
            // outline. Edges only, not corners, which is what rounds a
            // diagonal off rather than stair-stepping it in black.
            internal void Outline()
            {
                var outline = new List<(int, int)>();
                for (var y = -1; y <= N; y++)
                {
                    for (var x = -1; x <= N; x++)
                    {
                        if (this[x, y] != BuddyPixel.Transparent && In(x, y)) continue;
                        if (Solid(x - 1, y) || Solid(x + 1, y) || Solid(x, y - 1) || Solid(x, y + 1)) outline.Add((x, y));
                    }
                }

                foreach (var (x, y) in outline) this[x, y] = BuddyPixel.Outline;
            }

            private bool Solid(int x, int y)
            {
                var p = this[x, y];
                return p != BuddyPixel.Transparent && p != BuddyPixel.Outline && p != BuddyPixel.Sparkle;
            }

            // The bob: everything one row up. The top row must be empty for
            // this to be lossless, which the clip count checks.
            internal void ShiftUp()
            {
                for (var x = 0; x < N; x++)
                {
                    if (Cells[x] != BuddyPixel.Transparent) Clipped++;
                }

                Array.Copy(Cells, N, Cells, 0, N * (N - 1));
                Array.Fill(Cells, BuddyPixel.Transparent, N * (N - 1), N);
            }
        }
    }
}
