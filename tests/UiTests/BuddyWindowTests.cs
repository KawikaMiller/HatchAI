using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace HatchAI.Tests;

// The companion, its bubble and its card, driven headless through the same
// calls BuddyController makes (IBuddyView) and the same clicks a person would.
//
// No real timer anywhere: every BuddyWindow here is built with animate: false
// and a clock the test owns, and time moves only when a test calls Tick. The
// pointer path is safe to drive for real — a click on the buddy opens its
// card and nothing else; nothing here reaches TerminalFocuser. The one path
// not driven is a press that turns into a drag, which hands the pointer to
// the OS's own move loop (BuddyWindow.BeginDrag, excluded from coverage).
//
// Joins the Settings collection: the companion reads and writes its position
// and the two buddy preferences.
[Collection("Settings")]
public class BuddyWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    internal static BuddyGenome Genome(
        BuddySpecies species = BuddySpecies.Duck,
        BuddyRarity rarity = BuddyRarity.Rare,
        bool shiny = false) =>
        new(species, BuddyTaxonomy.FamilyOf(species), rarity, shiny, BuddyEyes.Dot, BuddyHat.Crown,
            new BuddyStats(80, 40, 12, 55, 60), BuddyPersonality.Cheerful, BuddyPersonality.Curious, "Pip");

    internal static BuddyState State(long tokens, long lifetime = -1, string? name = null, params BuddyHistoryEntry[] history) =>
        BuddyState.Hatch("uuid", T0) with
        {
            Tokens = tokens,
            LifetimeTokens = lifetime < 0 ? tokens : lifetime,
            Stars = BuddyProgress.StarsFor(tokens),
            Name = name,
            History = history,
            Rebirths = history.Length,
        };

    private sealed class Clock
    {
        internal DateTimeOffset Now = T0;
    }

    private static (BuddyWindow Window, Clock Clock) NewWindow()
    {
        HatchAISettings.ClearOrbPosition(BuddyWindow.PositionKey);
        HatchAISettings.BuddyBubblesEnabled = true;
        HatchAISettings.BuddyEnabled = true;

        var clock = new Clock();
        var window = new BuddyWindow(() => clock.Now, animate: false) { WorkAreaAt = _ => Work };
        return (window, clock);
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // ---- what the companion shows ------------------------------------------

    [AvaloniaFact]
    public void ShowsTheStageAndNameTheStateEarned()
    {
        var (window, _) = NewWindow();
        IBuddyView view = window;

        view.Show(Genome(), State(25_000));
        Flush();

        Assert.True(window.IsVisible);
        Assert.Equal(BuddyStage.Second, window.SpriteControl.Stage);
        Assert.Equal("Pip", window.NameShown);
        Assert.Contains("Second evolution", window.TipShown);
        Assert.Contains("25k tokens", window.TipShown);
        Assert.Contains("Rare Duck", window.TipShown);
        Assert.NotNull(window.SpriteControl.Grid);
        Assert.Equal(Genome(), window.SpriteControl.Genome);
        window.Close();
    }

    [AvaloniaFact]
    public void AChosenNameWinsAndStarsRideOnTheNameTag()
    {
        var (window, _) = NewWindow();

        window.ShowBuddy(Genome(), State(130_000, name: "Captain"));

        Assert.Equal("Captain ★★", window.NameShown);
        Assert.Equal(BuddyStage.Third, window.SpriteControl.Stage);
        window.Close();
    }

    // Every buddy starts as an egg (CB-195), and the companion says so.
    [AvaloniaFact]
    public void ANewBuddyIsShownAsAnEgg()
    {
        var (window, _) = NewWindow();

        window.ShowBuddy(Genome(), State(1_000));

        Assert.Equal(BuddyStage.Egg, window.SpriteControl.Stage);
        Assert.Contains("Egg · 1k tokens", window.TipShown);
        Assert.False(window.RebirthMenuItem.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void ShowingAgainRedrawsInPlace()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(5_000));
        var at = window.Position;

        window.ShowBuddy(Genome(), State(15_000));

        Assert.Equal(BuddyStage.First, window.SpriteControl.Stage);
        Assert.Equal(at, window.Position);
        window.Close();
    }

    [AvaloniaFact]
    public void UpdateStateRefreshesWithoutBringingAHiddenBuddyBack()
    {
        var (window, _) = NewWindow();
        IBuddyView view = window;

        view.UpdateState(Genome(), State(23_000));

        Assert.False(window.IsVisible);
        Assert.Equal(BuddyStage.Second, window.SpriteControl.Stage);
        Assert.Equal(23_000, window.State!.Tokens);
        window.Close();
    }

    // ---- rebirth -------------------------------------------------------------

    // From 42,500, a full 10k after Third begins at 32,500 (CB-195).
    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(32_500, false)]
    [InlineData(42_499, false)]
    [InlineData(42_500, true)]
    [InlineData(500_000, true)]
    public void RebirthIsOfferedFromTheRebirthThreshold(long tokens, bool offered)
    {
        var (window, _) = NewWindow();

        window.ShowBuddy(Genome(), State(tokens));
        window.OpenCard();

        Assert.Equal(offered, window.RebirthMenuItem.IsEnabled);
        Assert.Equal(offered, window.Card!.OffersRebirth);
        window.Close();
    }

    [AvaloniaFact]
    public void TheMenuOpensTheCardAtTheConfirmationAndAcceptingRaisesRebirthRequested()
    {
        var (window, _) = NewWindow();
        var requested = 0;
        ((IBuddyView)window).RebirthRequested += () => requested++;
        window.ShowBuddy(Genome(), State(43_000));

        Click(window.RebirthMenuItem);
        Flush();

        var card = window.Card!;
        Assert.True(card.IsVisible);
        Assert.True(card.IsConfirmingRebirth);
        Assert.Contains("Pip retires to your past buddies", card.ConfirmShown);
        Assert.Equal(0, requested);

        Click(card.ConfirmControl);

        Assert.Equal(1, requested);
        Assert.False(card.IsConfirmingRebirth);
        window.Close();
    }

    [AvaloniaFact]
    public void TheCardsOwnButtonAsksFirstAndKeepBacksOut()
    {
        var (window, _) = NewWindow();
        var requested = 0;
        window.RebirthRequested += () => requested++;
        window.ShowBuddy(Genome(), State(45_000));
        window.OpenCard();
        var card = window.Card!;

        Click(card.RebirthControl);
        Assert.True(card.IsConfirmingRebirth);
        Assert.False(card.OffersRebirth);

        Click(card.CancelControl);
        Assert.False(card.IsConfirmingRebirth);
        Assert.True(card.OffersRebirth);
        Assert.Equal(0, requested);
        window.Close();
    }

    [AvaloniaFact]
    public void OfferingRebirthBelowTheThresholdDoesNothing()
    {
        // Third, but short of the threshold.
        var card = new BuddyCard();
        card.UpdateFrom(Genome(), State(32_500));

        card.OfferRebirth();

        Assert.False(card.IsConfirmingRebirth);
        Assert.False(card.OffersRebirth);
    }

    [AvaloniaFact]
    public void AStateThatLosesEligibilityMidConfirmationWithdrawsIt()
    {
        var card = new BuddyCard();
        card.UpdateFrom(Genome(), State(43_000));
        card.OfferRebirth();
        Assert.True(card.IsConfirmingRebirth);

        // The controller rebirthed from somewhere else: tokens are back to 0.
        card.UpdateFrom(Genome(), State(0));

        Assert.False(card.IsConfirmingRebirth);
        Assert.False(card.OffersRebirth);
    }

    // ---- hide and show -------------------------------------------------------

    [AvaloniaFact]
    public void HideAndShowThroughTheSeam()
    {
        var (window, clock) = NewWindow();
        IBuddyView view = window;
        view.Show(Genome(), State(1_000));
        view.ShowBubble("hello");
        window.OpenCard();

        view.Hide();

        Assert.False(window.IsVisible);
        Assert.False(window.Bubble!.IsVisible);
        Assert.False(window.Card!.IsVisible);

        view.Show(Genome(), State(2_000));
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void TheHideMenuItemTurnsTheBuddyOffAndSaysSo()
    {
        var (window, _) = NewWindow();
        var hidden = 0;
        window.HideRequested += () => hidden++;
        window.ShowBuddy(Genome(), State(1_000));

        Click(window.HideMenuItem);

        Assert.False(window.IsVisible);
        Assert.False(HatchAISettings.BuddyEnabled);
        Assert.Equal(1, hidden);
        HatchAISettings.BuddyEnabled = true;
        window.Close();
    }

    [AvaloniaFact]
    public void TheAnimationTickerRunsOnlyWhileShown()
    {
        HatchAISettings.ClearOrbPosition(BuddyWindow.PositionKey);
        var window = new BuddyWindow { WorkAreaAt = _ => Work };

        Assert.False(window.IsTicking);
        window.ShowBuddy(Genome(), State(0));
        Assert.True(window.IsTicking);
        window.HideBuddy();
        Assert.False(window.IsTicking);
        window.Close();
    }

    [AvaloniaFact]
    public void EachTickAdvancesTheIdleFrame()
    {
        var (window, clock) = NewWindow();
        window.ShowBuddy(Genome(), State(0));

        for (var i = 0; i < BuddySprite.FrameCount + 3; i++) window.Tick(clock.Now);

        Assert.Equal(3, window.SpriteControl.Frame);
        window.Close();
    }

    // ---- the bubble ------------------------------------------------------------

    [AvaloniaFact]
    public void ABubbleShowsItsTextAndGoesOnTheInjectedClock()
    {
        var (window, clock) = NewWindow();
        IBuddyView view = window;
        view.Show(Genome(), State(0));

        view.ShowBubble("Nice one!");
        Flush();

        var bubble = window.Bubble!;
        Assert.True(bubble.IsVisible);
        Assert.Equal("Nice one!", bubble.Text);
        Assert.Equal(T0 + BubbleWindow.MinimumShown, bubble.Until);

        clock.Now = T0 + BubbleWindow.MinimumShown - TimeSpan.FromMilliseconds(1);
        window.Tick(clock.Now);
        Assert.True(bubble.IsVisible);

        clock.Now = T0 + BubbleWindow.MinimumShown;
        window.Tick(clock.Now);
        Assert.False(bubble.IsVisible);
        Assert.Null(bubble.Until);
        window.Close();
    }

    [AvaloniaFact]
    public void ABubbleSitsAboveTheBuddyWithItsTailPointingDown()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));

        window.ShowBubble("Thinking hard about this one.");

        var bubble = window.Bubble!;
        var companion = window.CompanionRect();
        Assert.Equal(BubbleSide.Above, bubble.Side);
        Assert.True(bubble.Position.Y + bubble.Height <= companion.Y);
        Assert.InRange(bubble.TailLeft, 12, bubble.Width);
        window.Close();
    }

    [AvaloniaFact]
    public void AgainstTheTopOfTheScreenTheBubbleDropsBelow()
    {
        var (window, _) = NewWindow();
        HatchAISettings.SetOrbPosition(BuddyWindow.PositionKey, 900, 0);
        window.ShowBuddy(Genome(), State(0));

        window.ShowBubble("Down here.");

        Assert.Equal(BubbleSide.Below, window.Bubble!.Side);
        Assert.True(window.Bubble.Position.Y >= window.CompanionRect().Bottom);
        window.Close();
    }

    [AvaloniaFact]
    public void HideBubbleThroughTheSeamAndClickToDismiss()
    {
        var (window, _) = NewWindow();
        IBuddyView view = window;
        view.Show(Genome(), State(0));

        view.ShowBubble("one");
        view.HideBubble();
        Assert.False(window.Bubble!.IsVisible);

        view.ShowBubble("two");
        Flush();
        var bubble = window.Bubble;
        bubble.MouseDown(new Point(10, 15), MouseButton.Left, RawInputModifiers.None);
        Flush();
        Assert.False(bubble.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void NoBubbleWhenBubblesAreOffOrTheBuddyIsHidden()
    {
        var (window, _) = NewWindow();

        window.ShowBubble("nobody home");
        Assert.Null(window.Bubble);

        window.ShowBuddy(Genome(), State(0));
        HatchAISettings.BuddyBubblesEnabled = false;
        window.ShowBubble("muted");
        Assert.Null(window.Bubble);
        HatchAISettings.BuddyBubblesEnabled = true;
        window.Close();
    }

    [AvaloniaFact]
    public void TheBubblesMenuItemTogglesThePreferenceAndTakesTheBubbleDown()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));
        Assert.True(window.BubblesMenuItem.IsChecked);
        window.ShowBubble("hi");

        Click(window.BubblesMenuItem);

        Assert.False(HatchAISettings.BuddyBubblesEnabled);
        Assert.False(window.BubblesMenuItem.IsChecked);
        Assert.False(window.Bubble!.IsVisible);

        Click(window.BubblesMenuItem);
        Assert.True(HatchAISettings.BuddyBubblesEnabled);
        Assert.True(window.BubblesMenuItem.IsChecked);
        window.Close();
    }

    [Fact]
    public void ABubbleStaysUpLongerForMoreToRead()
    {
        Assert.Equal(BubbleWindow.MinimumShown, BubbleWindow.DurationFor("hi"));
        Assert.Equal(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(60 * 50), BubbleWindow.DurationFor(new string('x', 50)));
        Assert.Equal(BubbleWindow.MaximumShown, BubbleWindow.DurationFor(new string('x', 500)));
    }

    // ---- position --------------------------------------------------------------

    [AvaloniaFact]
    public void FirstAppearanceIsTheBottomRightCorner()
    {
        var (window, _) = NewWindow();

        window.ShowBuddy(Genome(), State(0));

        Assert.Equal(BuddyPlacement.DefaultCompanion(new PixelSize(96, 114), Work), window.Position);
        window.Close();
    }

    [AvaloniaFact]
    public void ASavedSpotOnAMonitorThatIsGoneIsRescued()
    {
        var (window, _) = NewWindow();
        HatchAISettings.SetOrbPosition(BuddyWindow.PositionKey, -3000, 400);

        window.ShowBuddy(Genome(), State(0));

        Assert.Equal(new PixelPoint(0, 400), window.Position);
        window.Close();
    }

    // QA on CB-195: only a drag is the user's choice. The corner a first
    // appearance picks, and the spot a stranded buddy is pulled back to, are
    // the window's own, and saving either as if chosen would pin the buddy
    // there — the first across a screen-size change, the second over the
    // spot on the monitor that is only unplugged for now.
    [AvaloniaFact]
    public void PlacingItselfIsNeverSavedAsTheUsersChoice()
    {
        var (window, clock) = NewWindow();
        window.ShowBuddy(Genome(), State(0));
        Flush();
        window.Tick(clock.Now);
        Assert.Null(HatchAISettings.OrbPositionFor(BuddyWindow.PositionKey));
        window.Close();

        (window, clock) = NewWindow();
        HatchAISettings.SetOrbPosition(BuddyWindow.PositionKey, -3000, 400);
        window.ShowBuddy(Genome(), State(0));
        Flush();
        window.Tick(clock.Now);
        Assert.Equal(new HatchAISettings.OrbPlacement(-3000, 400), HatchAISettings.OrbPositionFor(BuddyWindow.PositionKey));
        window.Close();
    }

    [AvaloniaFact]
    public void AMoveIsSavedOnTheNextTickNotBefore()
    {
        var (window, clock) = NewWindow();
        window.ShowBuddy(Genome(), State(0));

        window.Position = new PixelPoint(300, 200);
        Assert.Null(HatchAISettings.OrbPositionFor(BuddyWindow.PositionKey));

        window.Tick(clock.Now);

        Assert.Equal(new HatchAISettings.OrbPlacement(300, 200), HatchAISettings.OrbPositionFor(BuddyWindow.PositionKey));
        window.Close();
    }

    [AvaloniaFact]
    public void MovingTheBuddyTakesItsBubbleAndCardAlong()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));
        window.ShowBubble("coming with you");
        window.OpenCard();
        Flush();

        window.Position = new PixelPoint(600, 600);

        Assert.False(new PixelRect(window.Bubble!.Position, new PixelSize((int)window.Bubble.Width, (int)window.Bubble.Height)).Intersects(window.CompanionRect()));
        Assert.True(window.Bubble.Position.Y < 600);
        Assert.False(new PixelRect(window.Card!.Position, new PixelSize(280, 10)).Intersects(window.CompanionRect()));
        window.Close();
    }

    [AvaloniaFact]
    public void ADisplayChangeRescuesAStrandedBuddyWithoutForgettingItsSpot()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));
        window.Position = new PixelPoint(2500, 300);
        window.FlushPosition();

        window.RescueOffScreen();

        Assert.Equal(new PixelPoint(1920 - 96, 300), window.Position);
        window.Tick(DateTimeOffset.Now);
        Assert.Equal(new HatchAISettings.OrbPlacement(2500, 300), HatchAISettings.OrbPositionFor(BuddyWindow.PositionKey));

        // Already on screen: nothing moves.
        window.RescueOffScreen();
        Assert.Equal(new PixelPoint(1920 - 96, 300), window.Position);
        window.Close();
    }

    [AvaloniaFact]
    public void WithNoWorkAreaAnywhereTheBuddyStaysPut()
    {
        var (window, _) = NewWindow();
        window.WorkAreaAt = _ => null;
        window.Position = new PixelPoint(7, 9);

        window.PlaceOnScreen();
        window.RescueOffScreen();
        Assert.Equal(new PixelPoint(7, 9), window.Position);

        // The bubble and card still come up, beside the buddy and off it.
        window.ShowBuddy(Genome(), State(0));
        window.ShowBubble("where am I");
        window.OpenCard();
        Assert.Equal(new PixelPoint(7, 9), window.Position);
        Assert.False(new PixelRect(window.Bubble!.Position, new PixelSize(136, 39)).Intersects(window.CompanionRect()));
        window.Close();
    }

    [AvaloniaFact]
    public void TheRealScreensAreAskedWhenNoWorkAreaIsInjected()
    {
        HatchAISettings.ClearOrbPosition(BuddyWindow.PositionKey);
        var window = new BuddyWindow(() => T0, animate: false);

        // Whatever the headless platform reports, the answer must be on it.
        window.ShowBuddy(Genome(), State(0));
        window.ShowBubble("hello");

        Assert.True(window.IsVisible);
        window.Close();
    }

    // ---- the card, from the companion ------------------------------------------

    [AvaloniaFact]
    public void ClickingTheBuddyTogglesItsCard()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));
        Flush();

        window.MouseDown(new Point(48, 48), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(new Point(48, 48), MouseButton.Left, RawInputModifiers.None);
        Flush();
        Assert.True(window.Card!.IsVisible);

        window.MouseDown(new Point(48, 48), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(new Point(48, 48), MouseButton.Left, RawInputModifiers.None);
        Flush();
        Assert.False(window.Card.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ARightClickOrAStrayReleaseDoesNotOpenTheCard()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));
        Flush();

        window.MouseDown(new Point(48, 48), MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(new Point(48, 48), MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(new Point(48, 48), MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.Null(window.Card);
        window.Close();
    }

    [AvaloniaFact]
    public void AWobbleUnderTheDragThresholdIsStillAClick()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));
        Flush();

        window.MouseDown(new Point(48, 48), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(50, 49), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(50, 49), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(60, 60), RawInputModifiers.None);
        Flush();

        Assert.True(window.Card!.IsVisible);
        window.Close();
    }

    // Past the threshold the press is handed to the OS's own window drag.
    // Headless has no such thing, so all this can check is the half that is
    // ours: the press is spent on the drag, the bubble is put away, and the
    // release afterwards is not mistaken for a click.
    [AvaloniaFact]
    public void APressThatMovesIsADragNotAClick()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));
        window.ShowBubble("wheee");
        Flush();

        window.MouseDown(new Point(48, 48), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(70, 48), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(70, 48), MouseButton.Left, RawInputModifiers.None);
        Flush();

        Assert.Null(window.Card);
        Assert.False(window.Bubble!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void TheCardMenuItemAndTheCloseButton()
    {
        var (window, _) = NewWindow();
        window.ShowBuddy(Genome(), State(0));

        Click(window.CardMenuItem);
        Flush();
        var card = window.Card!;
        Assert.True(card.IsVisible);

        var close = card.CloseControl;
        var centre = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), card)!.Value;
        card.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        Flush();
        Assert.False(card.IsVisible);

        // Showing new state while the card is open redraws it too.
        window.OpenCard();
        window.ShowBuddy(Genome(), State(13_000));
        Assert.Equal("First evolution", card.StageShown);
        window.Close();
    }

    [AvaloniaFact]
    public void NoCardBeforeTheresABuddy()
    {
        var (window, _) = NewWindow();

        window.OpenCard();

        Assert.Null(window.Card);
        window.Close();
    }
}
