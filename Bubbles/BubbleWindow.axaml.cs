using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace HatchAI
{
    // The buddy's speech bubble. Owned by E3 (CB-195).
    //
    // It has no timer of its own. It is told when it was shown and asked, on
    // every tick of the companion's clock, whether its time is up — so the
    // headless suite can walk a clock forward and watch it go, with no real
    // timer anywhere in the test and no sleep. BuddyWindow owns the one
    // ticker; this owns only the arithmetic.
    internal partial class BubbleWindow : Window
    {
        // Long enough to read a short line twice, not so long that a bubble
        // is still up when the next moment wants one. Scaled by length within
        // those bounds, at roughly a fast reader's pace.
        internal static readonly TimeSpan MinimumShown = TimeSpan.FromSeconds(4);
        internal static readonly TimeSpan MaximumShown = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PerCharacter = TimeSpan.FromMilliseconds(60);

        // Where the tail sits along the bubble, and how far from either end
        // it is allowed to go — past this it would hang off the rounded
        // corner instead of the straight edge.
        private const double TailInset = 12;
        private const double TailWidth = 12;

        private DateTimeOffset? _until;

        public BubbleWindow()
        {
            InitializeComponent();

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();
                this.AcceptFirstClick();
            };

            // A click is "got it". The bubble never takes focus, so this is
            // the one way to be rid of it early.
            Root.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                Dismiss();
            };
        }

        internal string Text => BubbleText.Text ?? string.Empty;

        // When the bubble goes, or null when it is not up.
        internal DateTimeOffset? Until => _until;

        internal BubbleSide Side { get; private set; } = BubbleSide.Above;

        internal static TimeSpan DurationFor(string text)
        {
            var span = TimeSpan.FromSeconds(2) + PerCharacter * text.Length;
            if (span < MinimumShown) return MinimumShown;
            return span > MaximumShown ? MaximumShown : span;
        }

        // Sets the text and the deadline. Placement is separate (Place) and
        // showing is the caller's, because only the companion knows where it
        // is.
        internal void SetText(string text, DateTimeOffset now)
        {
            BubbleText.Text = text;
            _until = now + DurationFor(text);
        }

        // True when this tick took the bubble down.
        internal bool Tick(DateTimeOffset now)
        {
            if (_until is not { } until || now < until) return false;
            Dismiss();
            return true;
        }

        internal void Dismiss()
        {
            _until = null;
            Hide();
        }

        // The size the bubble wants, measured from its content rather than
        // read off the window — see BubbleWindow.axaml's Root.
        internal Size ContentSize()
        {
            Root.Measure(Size.Infinity);
            return Root.DesiredSize;
        }

        // Positions the window beside `companion` (screen pixels, the whole
        // footprint the companion's window may occupy) and turns the tail
        // towards `pointAtX`, the screen x of the sprite's middle — which is
        // not the footprint's middle when Windows has widened the window.
        internal void Place(PixelRect companion, int pointAtX, PixelRect workArea, double scaling)
        {
            var content = ContentSize();
            Width = content.Width;
            Height = content.Height;

            var pixels = BuddyPlacement.Footprint(new PixelSize(
                (int)Math.Ceiling(content.Width * scaling),
                (int)Math.Ceiling(content.Height * scaling)));

            var at = BuddyPlacement.Bubble(companion, pixels, workArea);
            Side = BuddyPlacement.SideOf(at, pixels, companion);
            Position = at;

            TailUp.Opacity = Side == BubbleSide.Below ? 1 : 0;
            TailDown.Opacity = Side == BubbleSide.Above ? 1 : 0;

            // Point at the middle of the companion, kept off the corners.
            var centre = (pointAtX - at.X) / scaling - TailWidth / 2;
            var max = Math.Max(TailInset, content.Width - TailInset - TailWidth);
            var left = Math.Clamp(centre, TailInset, max);
            TailUp.Margin = TailDown.Margin = new Thickness(left, 0, 0, 0);
        }

        internal double TailLeft => TailDown.Margin.Left;
    }
}
