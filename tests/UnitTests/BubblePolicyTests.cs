using Xunit;

namespace HatchAI.Tests;

public class BubblePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static BubblePolicyState Shown(BuddyMoment moment, DateTimeOffset at) =>
        new(at, new Dictionary<BuddyMoment, DateTimeOffset> { [moment] = at });

    [Fact]
    public void AFirstMomentIsShownAndRecorded()
    {
        var d = BubblePolicy.Decide(BuddyMoment.Responded, BubblePolicyState.Empty, true, Now);

        Assert.True(d.Show);
        Assert.Null(d.RetryAt);
        Assert.Equal(Now, d.Next.LastShown);
        Assert.Equal(Now, d.Next.LastShownByMoment[BuddyMoment.Responded]);
    }

    [Fact]
    public void DecideDoesNotMutateTheStateItWasGiven()
    {
        var before = BubblePolicyState.Empty;
        BubblePolicy.Decide(BuddyMoment.Responded, before, true, Now);

        Assert.Null(before.LastShown);
        Assert.Empty(before.LastShownByMoment);
    }

    [Fact]
    public void DisabledIsFinalAndChangesNothing()
    {
        var state = Shown(BuddyMoment.Thinking, Now.AddMinutes(-5));
        var d = BubblePolicy.Decide(BuddyMoment.NeedsAttention, state, false, Now);

        Assert.False(d.Show);
        Assert.Null(d.RetryAt);
        Assert.Same(state, d.Next);
    }

    [Fact]
    public void InsideTheGlobalGapIsDeferredNotDropped()
    {
        var state = Shown(BuddyMoment.Responded, Now.AddSeconds(-5));
        var d = BubblePolicy.Decide(BuddyMoment.NeedsAttention, state, true, Now);

        Assert.False(d.Show);
        Assert.Equal(Now.AddSeconds(-5) + BubblePolicy.MinimumGap, d.RetryAt);
        Assert.Same(state, d.Next);
    }

    [Fact]
    public void ExactlyAtTheGapItIsShown()
    {
        var state = Shown(BuddyMoment.Responded, Now - BubblePolicy.MinimumGap);

        Assert.True(BubblePolicy.Decide(BuddyMoment.NeedsAttention, state, true, Now).Show);
    }

    [Fact]
    public void ARecentSameMomentIsDroppedForGood()
    {
        // 25 s ago: past the 20 s gap, inside Responded's own cooldown.
        var state = Shown(BuddyMoment.Responded, Now.AddSeconds(-25));
        var d = BubblePolicy.Decide(BuddyMoment.Responded, state, true, Now);

        Assert.False(d.Show);
        Assert.Null(d.RetryAt);
        Assert.Same(state, d.Next);
    }

    [Fact]
    public void ADifferentMomentIsNotHeldByAnotherOnesCooldown()
    {
        var state = Shown(BuddyMoment.Responded, Now.AddSeconds(-25));

        Assert.True(BubblePolicy.Decide(BuddyMoment.NeedsAttention, state, true, Now).Show);
    }

    [Fact]
    public void ACooldownEndsAndTheMomentSpeaksAgain()
    {
        var state = Shown(BuddyMoment.Responded, Now - BubblePolicy.CooldownFor(BuddyMoment.Responded));

        Assert.True(BubblePolicy.Decide(BuddyMoment.Responded, state, true, Now).Show);
    }

    [Fact]
    public void EvolvedHasNoCooldownOfItsOwnButStillObeysTheGap()
    {
        var state = Shown(BuddyMoment.Evolved, Now.AddSeconds(-1));
        var d = BubblePolicy.Decide(BuddyMoment.Evolved, state, true, Now);

        Assert.False(d.Show);
        Assert.NotNull(d.RetryAt);

        Assert.True(BubblePolicy.Decide(BuddyMoment.Evolved, state, true, Now.AddSeconds(30)).Show);
    }

    [Fact]
    public void ShowingKeepsWhatWasShownBefore()
    {
        var state = Shown(BuddyMoment.Responded, Now.AddMinutes(-10));
        var next = BubblePolicy.Decide(BuddyMoment.Thinking, state, true, Now).Next;

        Assert.Equal(Now, next.LastShown);
        Assert.Equal(Now.AddMinutes(-10), next.LastShownByMoment[BuddyMoment.Responded]);
        Assert.Equal(Now, next.LastShownByMoment[BuddyMoment.Thinking]);
    }

    [Fact]
    public void PriorityIsAStrictOrderWhereTheSpecSaysItIs()
    {
        var order = new[]
        {
            BuddyMoment.NeedsAttention, BuddyMoment.Evolved, BuddyMoment.Responded,
            BuddyMoment.UserResponded, BuddyMoment.Thinking, BuddyMoment.SessionStarted,
            BuddyMoment.LongIdle,
        };
        for (var i = 1; i < order.Length; i++)
            Assert.True(BubblePolicy.Priority(order[i - 1]) > BubblePolicy.Priority(order[i]),
                $"{order[i - 1]} should outrank {order[i]}");

        Assert.Equal(BubblePolicy.Priority(BuddyMoment.SessionStarted), BubblePolicy.Priority(BuddyMoment.SessionEnded));
    }

    [Fact]
    public void EveryMomentHasACooldownAndAPriority()
    {
        foreach (var m in Enum.GetValues<BuddyMoment>())
        {
            Assert.True(BubblePolicy.CooldownFor(m) >= TimeSpan.Zero);
            Assert.True(BubblePolicy.Priority(m) > 0);
        }

        Assert.Equal(TimeSpan.FromMinutes(10), BubblePolicy.CooldownFor(BuddyMoment.LongIdle));
        Assert.Equal(TimeSpan.FromMinutes(2), BubblePolicy.CooldownFor(BuddyMoment.SessionEnded));
    }
}
