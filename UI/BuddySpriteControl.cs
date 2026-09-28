using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace HatchAI
{
    // Draws one BuddySprite grid as square pixels. Owned by E3 (CB-195).
    //
    // All the decisions are BuddySprite's — which cell is which role, and
    // which colour each role is. This only turns roles into rectangles, which
    // is why it is small enough to trust without a test of its own beyond
    // "it drew something": the screenshot suite is where what it draws gets
    // looked at.
    //
    // The pixel size comes from the control's own Width, set by whoever places
    // it, never from the window — see BuddyWindow.axaml on why a window's size
    // is not a layout input here. A whole number of device pixels per sprite
    // pixel is what keeps the art crisp, so the size is floored rather than
    // stretched to fill.
    internal sealed class BuddySpriteControl : Control
    {
        private BuddyGenome? _genome;
        private BuddyStage _stage;
        private int _frame;

        // Rebuilt only when what is drawn changes, not on every render: the
        // idle loop renders four times a second for as long as the buddy is
        // on screen.
        private BuddySpriteGrid? _grid;
        private IBrush?[] _brushes = Array.Empty<IBrush?>();

        internal BuddyGenome? Genome
        {
            get => _genome;
            set
            {
                if (Equals(_genome, value)) return;
                _genome = value;
                _brushes = value is null ? Array.Empty<IBrush?>() : Brushes(value);
                Rebuild();
            }
        }

        internal BuddyStage Stage
        {
            get => _stage;
            set
            {
                if (_stage == value) return;
                _stage = value;
                Rebuild();
            }
        }

        internal int Frame
        {
            get => _frame;
            set
            {
                if (_frame == value) return;
                _frame = value;
                Rebuild();
            }
        }

        // What is on screen now, for the headless suite to read back as ASCII
        // rather than as pixels.
        internal BuddySpriteGrid? Grid => _grid;

        private void Rebuild()
        {
            _grid = _genome is null ? null : BuddySprite.Rasterize(_genome, _stage, _frame);
            InvalidateVisual();
        }

        private static IBrush?[] Brushes(BuddyGenome genome)
        {
            var palette = BuddySprite.Palette(genome);
            var brushes = new IBrush?[palette.Length];
            for (var i = 0; i < palette.Length; i++)
            {
                if (palette[i] >> 24 == 0) continue;
                brushes[i] = new ImmutableSolidColorBrush(Color.FromUInt32(palette[i]));
            }

            return brushes;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_grid is null) return;

            var cell = Math.Max(1, Math.Floor(Math.Min(Bounds.Width, Bounds.Height) / _grid.Width));

            var offsetX = Math.Floor((Bounds.Width - cell * _grid.Width) / 2);
            var offsetY = Math.Floor((Bounds.Height - cell * _grid.Height) / 2);

            for (var y = 0; y < _grid.Height; y++)
            {
                for (var x = 0; x < _grid.Width; x++)
                {
                    var brush = _brushes[(int)_grid.At(x, y)];
                    if (brush is null) continue;
                    context.FillRectangle(brush, new Rect(offsetX + x * cell, offsetY + y * cell, cell, cell));
                }
            }
        }
    }
}
