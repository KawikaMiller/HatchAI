namespace HatchAI
{
    // What the rate limiter remembers between decisions. Kept by the
    // controller, never persisted: a restart forgetting when the last bubble
    // was shown costs at most one extra bubble.
    internal sealed record BubblePolicyState(
        DateTimeOffset? LastShown,
        IReadOnlyDictionary<BuddyMoment, DateTimeOffset> LastShownByMoment)
    {
        internal static readonly BubblePolicyState Empty =
            new(null, new Dictionary<BuddyMoment, DateTimeOffset>());
    }

    // RetryAt is what makes "deferred, not dropped" expressible: set only when
    // the global gap is what said no, it is when asking again would succeed.
    // Null with Show false means the answer is final — bubbles are off, or the
    // moment's own cooldown says it was said too recently to say again.
    internal readonly record struct BubbleDecision(
        bool Show, BubblePolicyState Next, DateTimeOffset? RetryAt = null);

    // Whether a moment earns a bubble right now. Pure. `enabled` is the
    // buddyBubblesEnabled setting passed in, not read, so the off switch is a
    // unit-test case like any other arm.
    //
    // The shape is TurnSoundPolicy's: a floor on how often the app speaks at
    // all, so a burst of moments reads as one remark, and a signal that lands
    // inside the floor is deferred rather than dropped, because nothing
    // re-raises a transition that has already happened.
    internal static class BubblePolicy
    {
        internal static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(20);

        // How soon the same kind of moment may speak again. Zero for the ones
        // that are rare and always worth saying.
        internal static TimeSpan CooldownFor(BuddyMoment moment) => moment switch
        {
            BuddyMoment.NeedsAttention => TimeSpan.FromSeconds(30),
            BuddyMoment.Evolved => TimeSpan.Zero,
            BuddyMoment.Responded => TimeSpan.FromSeconds(45),
            BuddyMoment.UserResponded => TimeSpan.FromSeconds(90),
            BuddyMoment.Thinking => TimeSpan.FromSeconds(90),
            BuddyMoment.SessionStarted => TimeSpan.FromMinutes(2),
            BuddyMoment.SessionEnded => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(10), // LongIdle
        };

        // Higher speaks first when several are waiting on the gap. Needing the
        // user outranks everything, evolution being the rare good news.
        internal static int Priority(BuddyMoment moment) => moment switch
        {
            BuddyMoment.NeedsAttention => 7,
            BuddyMoment.Evolved => 6,
            BuddyMoment.Responded => 5,
            BuddyMoment.UserResponded => 4,
            BuddyMoment.Thinking => 3,
            BuddyMoment.SessionStarted => 2,
            BuddyMoment.SessionEnded => 2,
            _ => 1, // LongIdle
        };

        internal static BubbleDecision Decide(
            BuddyMoment moment,
            BubblePolicyState state,
            bool enabled,
            DateTimeOffset now)
        {
            if (!enabled) return new BubbleDecision(false, state);

            if (state.LastShownByMoment.TryGetValue(moment, out var last)
                && now - last < CooldownFor(moment))
            {
                return new BubbleDecision(false, state);
            }

            if (state.LastShown is { } shown && now - shown < MinimumGap)
                return new BubbleDecision(false, state, shown + MinimumGap);

            var byMoment = new Dictionary<BuddyMoment, DateTimeOffset>(state.LastShownByMoment)
            {
                [moment] = now,
            };
            return new BubbleDecision(true, new BubblePolicyState(now, byMoment));
        }
    }
}
