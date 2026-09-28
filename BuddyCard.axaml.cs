using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace HatchAI
{
    // The buddy's card: who it is, how far it has come, and who came before.
    // Owned by E3 (CB-195).
    //
    // It changes nothing itself. Rebirth is two clicks — the offer, then the
    // confirmation naming what happens — and the second one raises
    // RebirthConfirmed for BuddyWindow to pass up as IBuddyView's
    // RebirthRequested. The controller re-rolls and calls Show, which is what
    // eventually redraws this card; nothing here pretends it already has.
    //
    // A confirmation panel inside the card rather than a dialog, for the
    // reason OrbWindow's lifecycle items give: this app has no dialog
    // vocabulary anywhere. Rebirth is the one destructive-sounding action the
    // buddy has — even though it keeps everything in history — so unlike the
    // orb's menu items it does ask.
    internal partial class BuddyCard : Window
    {
        private static readonly IBrush StatFill = new ImmutableSolidColorBrush(Color.Parse("#FF8AB4F8"));
        private static readonly IBrush PeakFill = new ImmutableSolidColorBrush(Color.Parse("#FFFFC857"));
        private static readonly IBrush Track = new ImmutableSolidColorBrush(Color.Parse("#1AFFFFFF"));
        private static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.Parse("#9EFFFFFF"));
        private static readonly IBrush Bright = new ImmutableSolidColorBrush(Color.Parse("#EBFFFFFF"));
        private static readonly IBrush Star = new ImmutableSolidColorBrush(Color.Parse("#FFFFC857"));

        private double _progressFraction;

        public BuddyCard()
        {
            InitializeComponent();

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();
                this.AcceptFirstClick();
            };

            CloseButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                CloseRequested?.Invoke();
            };

            RebirthButton.Click += (_, _) => OfferRebirth();
            CancelRebirthButton.Click += (_, _) => WithdrawRebirth();
            ConfirmRebirthButton.Click += (_, _) =>
            {
                WithdrawRebirth();
                RebirthConfirmed?.Invoke();
            };

            // The bar is a fraction of a width only layout knows; see
            // UsageCard's constructor for the same arrangement.
            ProgressTrack.SizeChanged += (_, _) => ApplyProgress();
        }

        internal event Action? RebirthConfirmed;
        internal event Action? CloseRequested;

        // Read back by the headless suite, which asserts on what a person
        // would have read rather than on how it was laid out.
        internal string NameShown => NameText.Text ?? string.Empty;
        internal string KindShown => KindText.Text ?? string.Empty;
        internal string StageShown => StageText.Text ?? string.Empty;
        internal string PersonalityShown => PersonalityText.Text ?? string.Empty;
        internal string NextShown => NextText.Text ?? string.Empty;
        internal string ProgressShown => ProgressText.Text ?? string.Empty;
        internal string LifetimeShown => LifetimeText.Text ?? string.Empty;
        internal string? LifetimeTip => ToolTip.GetTip(LifetimeText) as string;
        internal double ProgressFraction => _progressFraction;
        internal bool OffersRebirth => RebirthButton.IsVisible;
        internal bool IsConfirmingRebirth => ConfirmPanel.IsVisible;
        internal string ConfirmShown => ConfirmText.Text ?? string.Empty;
        internal Button RebirthControl => RebirthButton;
        internal Button ConfirmControl => ConfirmRebirthButton;
        internal Button CancelControl => CancelRebirthButton;
        internal Control CloseControl => CloseButton;
        internal BuddySpriteControl PortraitControl => Portrait;

        internal IReadOnlyList<string> HistoryShown =>
            HistoryRows.Children.OfType<Control>().Select(RowText).ToList();

        internal IReadOnlyList<string> StatsShown =>
            StatRows.Children.OfType<Control>().Select(RowText).ToList();

        internal void UpdateFrom(BuddyGenome genome, BuddyState state)
        {
            var stage = BuddyProgress.StageFor(state.Tokens);
            var stars = BuddyProgress.StarsFor(state.Tokens);
            var name = state.Name ?? genome.Name;

            Portrait.Genome = genome;
            Portrait.Stage = stage;

            NameText.Text = name;
            KindText.Text = KindLine(genome.Rarity, genome.Species, genome.Shiny);
            StageText.Text = StageLine(stage, stars);
            PersonalityText.Text = $"{genome.Primary} · {genome.Secondary}";

            BuildStats(genome.Stats);
            ApplyNext(state.Tokens);

            var buddies = state.History.Count + 1;
            LifetimeText.Text = $"Lifetime: {TokenFormat.Compact(state.LifetimeTokens)} tokens · {Plural(buddies, "buddy", "buddies")}";
            ToolTip.SetTip(LifetimeText,
                $"{state.LifetimeTokens.ToString("N0", CultureInfo.InvariantCulture)} output tokens across {Plural(buddies, "buddy", "buddies")}");

            BuildHistory(state.History);

            var canRebirth = BuddyProgress.CanRebirth(state.Tokens);
            RebirthButton.IsVisible = canRebirth && !ConfirmPanel.IsVisible;
            if (!canRebirth) ConfirmPanel.IsVisible = false;
            ConfirmText.Text =
                $"{name} retires to your past buddies with everything it earned, and a new buddy hatches. " +
                "Your lifetime total carries on.";
        }

        // The first click of two. Also reachable from the companion's menu,
        // which opens the card straight at this step.
        internal void OfferRebirth()
        {
            if (!RebirthButton.IsVisible && !ConfirmPanel.IsVisible) return;
            RebirthButton.IsVisible = false;
            ConfirmPanel.IsVisible = true;
        }

        internal void WithdrawRebirth()
        {
            ConfirmPanel.IsVisible = false;
            RebirthButton.IsVisible = true;
        }

        // ---- lines ------------------------------------------------------

        internal static string StageName(BuddyStage stage) => stage switch
        {
            BuddyStage.Egg => "Egg",
            BuddyStage.Hatchling => "Hatchling",
            BuddyStage.First => "First evolution",
            BuddyStage.Second => "Second evolution",
            _ => "Third evolution",
        };

        internal static string StageLine(BuddyStage stage, int stars) =>
            stars > 0 ? $"{StageName(stage)} {new string('★', stars)}" : StageName(stage);

        internal static string KindLine(BuddyRarity rarity, BuddySpecies species, bool shiny) =>
            shiny ? $"Shiny {rarity} {species}" : $"{rarity} {species}";

        private static string Plural(int n, string one, string many) =>
            n == 1 ? $"1 {one}" : $"{n.ToString(CultureInfo.InvariantCulture)} {many}";

        // The milestone just passed and the one being worked towards, or null
        // past the last star, when there is nothing left to fill.
        internal static (long From, long To)? ProgressSpan(long tokens)
        {
            if (BuddyProgress.NextMilestone(tokens) is not { } next) return null;

            long from = 0;
            foreach (var m in BuddyProgress.Milestones())
            {
                if (m <= tokens && m > from) from = m;
            }

            return (from, next);
        }

        // What reaching the next milestone gives you, in words. An egg
        // "hatches" rather than reaching "Hatchling at", and the gap between
        // Third and the rebirth offer is named for the offer, so the bar
        // filling up across it reads as working towards something rather than
        // as rebirth already being on the table.
        internal static string NextLine(long tokens)
        {
            if (BuddyProgress.NextMilestone(tokens) is not { } next)
                return "Every star earned";

            var at = TokenFormat.Compact(next);
            var stage = BuddyProgress.StageFor(next);
            if (stage != BuddyProgress.StageFor(tokens))
                return stage == BuddyStage.Hatchling ? $"Hatches at {at}" : $"{StageName(stage)} at {at}";
            if (next == BuddyProgress.RebirthThreshold) return $"Rebirth available at {at}";
            return $"Veteran star {BuddyProgress.StarsFor(next)} at {at}";
        }

        private void ApplyNext(long tokens)
        {
            NextText.Text = NextLine(tokens);
            if (ProgressSpan(tokens) is { } span)
            {
                ProgressText.Text = $"{TokenFormat.Compact(tokens)} / {TokenFormat.Compact(span.To)}";
                _progressFraction = (double)(tokens - span.From) / (span.To - span.From);
            }
            else
            {
                ProgressText.Text = TokenFormat.Compact(tokens);
                _progressFraction = 1;
            }

            ApplyProgress();
        }

        private void ApplyProgress() =>
            ProgressFill.Width = Math.Max(0, ProgressTrack.Bounds.Width * _progressFraction);

        // ---- rows -------------------------------------------------------

        private void BuildStats(BuddyStats stats)
        {
            var values = new (string Label, int Value)[]
            {
                ("DEBUGGING", stats.Debugging),
                ("PATIENCE", stats.Patience),
                ("CHAOS", stats.Chaos),
                ("WISDOM", stats.Wisdom),
                ("SNARK", stats.Snark),
            };
            var peak = values.Max(v => v.Value);

            StatRows.Children.Clear();
            foreach (var (label, value) in values)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*,28") };
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 9.5,
                    Foreground = Muted,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                // A bar per stat, filled as a share of a fixed width
                // rather than measured, because five SizeChanged handlers for
                // five bars that never resize would be ceremony.
                var bar = new Border
                {
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = Track,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new Border
                    {
                        Height = 5,
                        CornerRadius = new CornerRadius(2.5),
                        Background = value == peak ? PeakFill : StatFill,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Width = 1.36 * Math.Clamp(value, 0, 100),
                    },
                };
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);

                var number = new TextBlock
                {
                    Text = value.ToString(CultureInfo.InvariantCulture),
                    FontSize = 9.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Bright,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(number, 2);
                row.Children.Add(number);

                StatRows.Children.Add(row);
            }
        }

        private void BuildHistory(IReadOnlyList<BuddyHistoryEntry> history)
        {
            HistoryRows.Children.Clear();
            HistoryHeader.IsVisible = history.Count > 0;
            HistoryRows.IsVisible = history.Count > 0;

            // Newest first on screen, though stored oldest first: the buddy you
            // just said goodbye to is the one you are looking for. The stage is
            // the bare enum name ("Third") rather than StageName, so a shiny
            // legendary's stars still fit on the row.
            foreach (var entry in history.Reverse())
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var who = new TextBlock
                {
                    FontSize = 10.5,
                    Foreground = Bright,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                who.Inlines!.Add(new Avalonia.Controls.Documents.Run(entry.Name) { FontWeight = FontWeight.SemiBold });
                who.Inlines.Add(new Avalonia.Controls.Documents.Run(
                    $"  {KindLine(entry.Rarity, entry.Species, entry.Shiny)} · {entry.Stage}") { Foreground = Muted });
                if (entry.Stars > 0)
                {
                    who.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + new string('★', entry.Stars)) { Foreground = Star });
                }

                row.Children.Add(who);

                var tokens = new TextBlock
                {
                    Text = TokenFormat.Compact(entry.Tokens),
                    FontSize = 10.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Bright,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                ToolTip.SetTip(tokens, $"{entry.Tokens.ToString("N0", CultureInfo.InvariantCulture)} output tokens");
                Grid.SetColumn(tokens, 1);
                row.Children.Add(tokens);

                HistoryRows.Children.Add(row);
            }
        }

        // One row's text, as a reader would run their eye along it.
        private static string RowText(Control row) =>
            string.Join(" ", row.GetLogicalDescendants().OfType<TextBlock>()
                .Select(t => t.Inlines is { Count: > 0 } inlines
                    ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text))
                    : t.Text ?? string.Empty));
    }
}
