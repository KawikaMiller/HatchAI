using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace HatchAI
{
    // The buddy companion on the desktop, its speech bubble and its card.
    // Owned by E3 (CB-195); implements IBuddyView for E4's controller.
    //
    // It draws what it is told and reports what the user asked for, and
    // decides nothing about progress: the stage, the stars and whether rebirth
    // is on offer all come from BuddyProgress, read off the state the
    // controller hands in. The two preferences its menu toggles — bubbles and
    // the buddy itself — are written straight to HatchAISettings, since
    // they are preferences rather than buddy state, and HideRequested tells
    // the controller the buddy was put away so it stops calling Show.
    //
    // One ticker, four times a second, while the companion is visible. It
    // advances the idle animation, asks the bubble whether its time is up, and
    // writes a dragged position to settings. Everything it calls is a plain
    // method taking the time, so the headless suite drives the same code with
    // a clock of its own and never waits on a real timer.
    internal partial class BuddyWindow : Window, IBuddyView
    {
        // The key in HatchAISettings' orbPositions map. Not a session id
        // or a directory, which is what every other key there is, so it
        // cannot collide with an orb.
        internal const string PositionKey = "buddy";

        internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

        // Must match Root in BuddyWindow.axaml. Named here because placement
        // needs it before layout has run, and because the window's own size is
        // not to be trusted — see the axaml.
        internal static readonly Size ContentSize = new(96, 114);

        // The sprite's middle, for the bubble's tail.
        private const double SpriteCentreX = 48;

        // How far the pointer moves before a press becomes a drag rather than
        // a click. Same order as OrbWindow's six pixels.
        private const double DragThreshold = 5;

        private readonly Func<DateTimeOffset> _clock;
        private readonly bool _animate;
        private DispatcherTimer? _ticker;

        private BuddyGenome? _genome;
        private BuddyState? _state;
        private BubbleWindow? _bubble;
        private BuddyCard? _card;

        // Set while this window moves itself, so the move is not mistaken for
        // a drag and written back as the user's chosen spot.
        private bool _placing;
        private PixelPoint? _unsavedPosition;

        private PointerPressedEventArgs? _press;
        private Point _pressAt;

        public BuddyWindow() : this(() => DateTimeOffset.Now, animate: true)
        {
        }

        // `animate` false for the headless suite, which advances Tick itself.
        internal BuddyWindow(Func<DateTimeOffset> clock, bool animate)
        {
            _clock = clock;
            _animate = animate;
            InitializeComponent();

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();

                // Otherwise the first click on the buddy is spent activating
                // the app and never reaches it — see AcceptFirstClick.
                this.AcceptFirstClick();
            };

            PositionChanged += (_, _) => OnMoved();
            Screens.Changed += (_, _) => RescueOffScreen();

            Closed += (_, _) =>
            {
                StopTicker();
                _bubble?.Close();
                _card?.Close();
            };

            Root.PointerPressed += OnRootPressed;
            Root.PointerMoved += OnRootMoved;
            Root.PointerReleased += OnRootReleased;
        }

        public event Action? RebirthRequested;

        // The user chose "Hide buddy". BuddyEnabled is already off by the time
        // this fires; the controller answers with Reapply, which is what stops
        // it counting and lets the tray or settings switch bring it back.
        public event Action? HideRequested;

        // Where the buddy's work area comes from. Screens, normally; the
        // headless suite supplies its own so placement is not at the mercy of
        // whatever display the test host pretends to have.
        internal Func<PixelPoint, PixelRect?>? WorkAreaAt { get; set; }

        internal BuddyState? State => _state;
        internal BuddySpriteControl SpriteControl => Sprite;
        internal string NameShown => NameTag.Text ?? string.Empty;
        internal string? TipShown => ToolTip.GetTip(Root) as string;
        internal MenuItem RebirthMenuItem => RebirthItem;
        internal MenuItem CardMenuItem => CardItem;
        internal MenuItem BubblesMenuItem => BubblesItem;
        internal MenuItem HideMenuItem => HideItem;
        internal BubbleWindow? Bubble => _bubble;
        internal BuddyCard? Card => _card;
        internal bool IsTicking => _ticker is not null;

        // ---- IBuddyView -------------------------------------------------

        void IBuddyView.Show(BuddyGenome genome, BuddyState state) => ShowBuddy(genome, state);

        void IBuddyView.UpdateState(BuddyGenome genome, BuddyState state) => UpdateState(genome, state);

        void IBuddyView.Hide() => HideBuddy();

        void IBuddyView.ShowBubble(string text) => ShowBubble(text);

        void IBuddyView.HideBubble() => _bubble?.Dismiss();

        internal void ShowBuddy(BuddyGenome genome, BuddyState state)
        {
            UpdateState(genome, state);
            if (IsVisible) return;

            PlaceOnScreen();
            Show();
            StartTicker();
        }

        // Everything Show draws, without showing. A hidden companion keeps
        // its latest state, so it comes back current rather than stale.
        internal void UpdateState(BuddyGenome genome, BuddyState state)
        {
            _genome = genome;
            _state = state;

            var stage = BuddyProgress.StageFor(state.Tokens);
            var stars = BuddyProgress.StarsFor(state.Tokens);
            var name = state.Name ?? genome.Name;

            Sprite.Genome = genome;
            Sprite.Stage = stage;
            NameTag.Text = stars > 0 ? $"{name} {new string('★', stars)}" : name;
            ToolTip.SetTip(Root,
                $"{name} — {BuddyCard.KindLine(genome.Rarity, genome.Species, genome.Shiny)}\n" +
                $"{BuddyCard.StageName(stage)} · {TokenFormat.Compact(state.Tokens)} tokens");

            RebirthItem.IsEnabled = BuddyProgress.CanRebirth(state.Tokens);
            BubblesItem.IsChecked = HatchAISettings.BuddyBubblesEnabled;

            _card?.UpdateFrom(genome, state);
        }

        internal void HideBuddy()
        {
            StopTicker();
            _bubble?.Dismiss();
            _card?.Hide();
            Hide();
        }

        internal void ShowBubble(string text)
        {
            // The controller already asks BubblePolicy, which is told whether
            // bubbles are on; this is the same preference read once more, so a
            // bubble in flight when the menu item was unticked does not land.
            if (!IsVisible || !HatchAISettings.BuddyBubblesEnabled) return;

            _bubble ??= new BubbleWindow();
            _bubble.SetText(text, _clock());
            PlaceBubble();
            _bubble.Show();
        }

        // ---- the ticker -------------------------------------------------

        private void StartTicker()
        {
            if (!_animate || _ticker is not null) return;
            _ticker = new DispatcherTimer { Interval = TickInterval };
            _ticker.Tick += OnTicker;
            _ticker.Start();
        }

        // Excluded from coverage: the one line a real timer reaches. Tick,
        // which is everything it does, is driven directly by the suite.
        [ExcludeFromCodeCoverage]
        private void OnTicker(object? sender, EventArgs e) => Tick(_clock());

        private void StopTicker()
        {
            _ticker?.Stop();
            _ticker = null;
        }

        internal void Tick(DateTimeOffset now)
        {
            Sprite.Frame = (Sprite.Frame + 1) % BuddySprite.FrameCount;
            _bubble?.Tick(now);
            FlushPosition();
        }

        // ---- position ---------------------------------------------------

        private PixelSize CompanionPixels() => new(
            (int)Math.Ceiling(ContentSize.Width * DesktopScaling),
            (int)Math.Ceiling(ContentSize.Height * DesktopScaling));

        // The rectangle the companion's window may cover, including whatever
        // Windows added to it — which is what anything placed beside it has to
        // stay out of. See BuddyPlacement.Footprint.
        internal PixelRect CompanionRect() => new(Position, BuddyPlacement.Footprint(CompanionPixels()));

        // An injected source is the only source: a test that says "no screen"
        // means it, rather than falling through to the headless platform's.
        private PixelRect? WorkAreaFor(PixelPoint point) =>
            WorkAreaAt is { } injected
                ? injected(point)
                : (Screens.ScreenFromPoint(point) ?? Screens.Primary)?.WorkingArea;

        // The saved spot, pulled onto a screen that still exists; or the
        // bottom-right corner the first time.
        internal void PlaceOnScreen()
        {
            var saved = HatchAISettings.OrbPositionFor(PositionKey);
            var wanted = saved is null ? (PixelPoint?)null : new PixelPoint(saved.X, saved.Y);
            if (WorkAreaFor(wanted ?? Position) is not { } work) return;

            var size = CompanionPixels();
            MoveTo(wanted is { } at
                ? BuddyPlacement.ClampCompanion(at, size, work)
                : BuddyPlacement.DefaultCompanion(size, work));
        }

        // A display came or went: bring the buddy back if it was left on a
        // screen that is no longer there, without touching the saved spot, so
        // plugging the monitor back in puts it back where it was.
        internal void RescueOffScreen()
        {
            if (WorkAreaFor(Position) is not { } work) return;
            var clamped = BuddyPlacement.ClampCompanion(Position, CompanionPixels(), work);
            if (clamped != Position) MoveTo(clamped);
        }

        private void MoveTo(PixelPoint at)
        {
            _placing = true;
            try
            {
                Position = at;
            }
            finally
            {
                _placing = false;
            }

            MoveSatellites();
        }

        private void OnMoved()
        {
            if (_placing) return;
            _unsavedPosition = Position;
            MoveSatellites();
        }

        // Written on the next tick rather than on every move: a drag raises a
        // move per pointer event, and each write is a whole settings file.
        internal void FlushPosition()
        {
            if (_unsavedPosition is not { } at) return;
            _unsavedPosition = null;
            HatchAISettings.SetOrbPosition(PositionKey, at.X, at.Y);
        }

        private void MoveSatellites()
        {
            if (_bubble?.IsVisible == true) PlaceBubble();
            if (_card?.IsVisible == true) PlaceCard();
        }

        private void PlaceBubble()
        {
            if (_bubble is null) return;
            var companion = CompanionRect();
            var work = WorkAreaFor(Position) ?? companion;
            var pointAt = Position.X + (int)Math.Round(SpriteCentreX * DesktopScaling);
            _bubble.Place(companion, pointAt, work, DesktopScaling);
        }

        private void PlaceCard()
        {
            if (_card is null) return;
            _card.Root.Measure(new Size(_card.Width, double.PositiveInfinity));
            var size = BuddyPlacement.Footprint(new PixelSize(
                (int)Math.Ceiling(_card.Width * DesktopScaling),
                (int)Math.Ceiling(_card.Root.DesiredSize.Height * DesktopScaling)));
            var companion = CompanionRect();
            var work = WorkAreaFor(Position) ?? companion;
            _card.Position = BuddyPlacement.Bubble(companion, size, work);
        }

        // ---- the card ---------------------------------------------------

        internal void OpenCard(bool offerRebirth = false)
        {
            if (_genome is null || _state is null) return;

            if (_card is null)
            {
                _card = new BuddyCard();
                _card.RebirthConfirmed += () => RebirthRequested?.Invoke();
                _card.CloseRequested += () => _card.Hide();
            }

            _card.UpdateFrom(_genome, _state);
            if (offerRebirth) _card.OfferRebirth();
            PlaceCard();
            _card.Show();
        }

        internal void ToggleCard()
        {
            if (_card?.IsVisible == true) _card.Hide();
            else OpenCard();
        }

        // ---- menu -------------------------------------------------------

        private void Card_Click(object? sender, RoutedEventArgs e) => OpenCard();

        private void Rebirth_Click(object? sender, RoutedEventArgs e) => OpenCard(offerRebirth: true);

        private void Bubbles_Click(object? sender, RoutedEventArgs e)
        {
            var on = !HatchAISettings.BuddyBubblesEnabled;
            HatchAISettings.BuddyBubblesEnabled = on;
            BubblesItem.IsChecked = on;
            if (!on) _bubble?.Dismiss();
        }

        private void Hide_Click(object? sender, RoutedEventArgs e)
        {
            HatchAISettings.BuddyEnabled = false;
            HideBuddy();
            HideRequested?.Invoke();
        }

        // ---- pointer ----------------------------------------------------

        // A press becomes a drag once the pointer has moved a few pixels, and
        // the drag is the window manager's own (BeginMoveDrag), so it moves at
        // the OS's pace and snaps the way every other window does. A press
        // that is released without moving is a click, which opens the card.
        //
        // Right-click is left alone: the ContextMenu has it.
        private void OnRootPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _press = e;
            _pressAt = e.GetPosition(this);
        }

        private void OnRootMoved(object? sender, PointerEventArgs e)
        {
            if (_press is null) return;
            var at = e.GetPosition(this);
            if (Math.Abs(at.X - _pressAt.X) < DragThreshold && Math.Abs(at.Y - _pressAt.Y) < DragThreshold) return;

            var press = _press;
            _press = null;
            BeginDrag(press);
        }

        // Hands the pointer to the OS's own window drag: on Windows a modal
        // loop inside SendMessage, on macOS a native drag session. Under the
        // headless platform BeginMoveDrag does nothing, so the suite reaches
        // this line but not the drag itself — what a finished drag does is
        // covered where it lands, in OnMoved and FlushPosition.
        private void BeginDrag(PointerPressedEventArgs press)
        {
            _bubble?.Dismiss();
            BeginMoveDrag(press);
        }

        private void OnRootReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_press is null) return;
            _press = null;
            ToggleCard();
        }
    }
}
