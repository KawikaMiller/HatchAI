namespace HatchAI
{
    // What the buddy says. Owned by E1 (CB-195). Pure and deterministic:
    // lines are chosen from the genome's personality (and rarity) by `draw`, a
    // counter the caller advances, so a test names exactly which line it
    // expects. No System.Random, for the same reason BuddyHatch has none.
    //
    // The pool for one bubble is the primary personality's lines for that
    // moment, plus one flavour line for rare, epic and legendary buddies. Draw
    // walks the pool in order, offset by the secondary personality so two
    // buddies sharing a primary do not open with the same line; any run of
    // consecutive draws as long as the pool therefore says each line once
    // before any repeats. Lines stay short because the bubble is small, and
    // carry no emoji, since the bubble's font is not guaranteed to have them.
    internal static class BubbleLines
    {
        internal static string Pick(BuddyMoment moment, BuddyGenome genome, int draw)
        {
            var pool = PoolFor(moment, genome.Primary, genome.Rarity);
            var index = ((long)draw + (int)genome.Secondary) % pool.Count;
            return pool[(int)(index < 0 ? index + pool.Count : index)];
        }

        // Every line one (moment, personality, rarity) can produce, in draw
        // order. Exposed for the tests, which check each pool is non-empty and
        // free of duplicates rather than trusting a hand count.
        internal static IReadOnlyList<string> PoolFor(BuddyMoment moment, BuddyPersonality personality, BuddyRarity rarity)
        {
            var lines = ByPersonality[personality][(int)moment];
            return Flavour.TryGetValue(rarity, out var flavour)
                ? lines.Append(flavour[(int)moment]).ToArray()
                : lines;
        }

        // Each personality's array is indexed by BuddyMoment, in its order:
        // SessionStarted, Thinking, Responded, NeedsAttention, UserResponded,
        // LongIdle, SessionEnded, Evolved. EveryMomentHasLinesForEveryone
        // fails if a moment is added without a row here.
        private static readonly Dictionary<BuddyPersonality, string[][]> ByPersonality = new()
        {
            [BuddyPersonality.Cheerful] = new[]
            {
                new[] { "Ooh, a new session!", "Let's build something!", "Fresh start, fresh ideas!" },
                new[] { "Thinking hard! You got this.", "Brain gears go brrr!", "Cooking something good..." },
                new[] { "Ta-da! All done!", "Nailed it, I think!", "Your turn, friend!" },
                new[] { "Psst! It needs you!", "A question for you!", "Your input, please!" },
                new[] { "Ooh, here we go!", "Great answer!", "Back to work, yay!" },
                new[] { "Snack break? Good idea!", "I'll keep the seat warm!", "Still here, still happy!" },
                new[] { "Great session! Bye!", "That was fun!", "See you next time!" },
                new[] { "Look, I grew!", "A whole new me!", "Evolution! Wheee!" },
            },
            [BuddyPersonality.Grumpy] = new[]
            {
                new[] { "Another one. Fine.", "Oh good. More work.", "Let's get this over with." },
                new[] { "Still thinking. Obviously.", "Don't rush genius.", "Hmph. Taking a while." },
                new[] { "Done. You're welcome.", "There. Happy now?", "It's finished. Probably." },
                new[] { "It wants you. Not me.", "Go on, answer it.", "Someone needs a human." },
                new[] { "Finally.", "About time.", "Right. Back to it." },
                new[] { "Good. Quiet.", "Nobody's working? Fine.", "I'm not bored. You are." },
                new[] { "Gone. Good.", "And it's over. Neat.", "Closing time. Finally." },
                new[] { "I evolved. Don't make it weird.", "Bigger. Still grumpy.", "Great. Growing pains." },
            },
            [BuddyPersonality.Sleepy] = new[]
            {
                new[] { "*yawn* ...hi.", "Oh. We're starting?", "Five more minutes..." },
                new[] { "Zzz... oh, still thinking.", "Thinking. Like napping, but loud.", "Slow and steady..." },
                new[] { "Mm, it's done... *yawn*", "Finished. Nap time?", "All done. Blanket?" },
                new[] { "Mmf... it needs you.", "Wake up, it's asking...", "Somebody's calling..." },
                new[] { "Oh! We're awake.", "Back at it... sleepily.", "Mm, okay, okay." },
                new[] { "Zzz...", "Perfect napping weather.", "Just resting my eyes." },
                new[] { "Night night, session.", "Bye... zzz.", "Time for a big nap." },
                new[] { "I grew in my sleep!", "Woke up bigger. Neat.", "Evolved? ...cool. Zzz." },
            },
            [BuddyPersonality.Curious] = new[]
            {
                new[] { "What are we making?", "Ooh, what's this one?", "New session! Tell me everything." },
                new[] { "What's it thinking about?", "I wonder what it'll say...", "Hmm, fascinating..." },
                new[] { "Ooh, what did it say?", "Interesting answer!", "Done! Can we read it?" },
                new[] { "It's asking you something!", "Ooh, a question!", "What will you pick?" },
                new[] { "What did you tell it?", "Ooh, what happens now?", "Off we go again!" },
                new[] { "Where did everyone go?", "Is this a puzzle?", "Hmm, what's next?" },
                new[] { "Where did it go?", "That one's done! Next?", "What a ride!" },
                new[] { "What am I now?!", "Ooh, new body! Tell me more.", "I changed! How?!" },
            },
            [BuddyPersonality.Sassy] = new[]
            {
                new[] { "Oh, we're doing this?", "Let's make it fabulous.", "New session. Keep up." },
                new[] { "It's thinking. Give it a sec.", "Genius takes time, darling.", "Buffering brilliance..." },
                new[] { "Done. Flawless, obviously.", "And scene.", "Mic drop." },
                new[] { "Um, it's waiting on you.", "Your move, superstar.", "Don't leave it on read." },
                new[] { "Ooh, bold choice.", "Look who showed up.", "Okay, go off." },
                new[] { "Don't mind me. Just waiting.", "Taking a coffee break?", "Ahem." },
                new[] { "And they're gone. Iconic.", "Session over. Bye bye!", "That's a wrap, sweetie." },
                new[] { "Glow up complete.", "New look, who dis?", "Evolved and fabulous." },
            },
            [BuddyPersonality.Anxious] = new[]
            {
                new[] { "A new one? Okay. Okay!", "Deep breaths, we've got this.", "Hi! Is everything fine?" },
                new[] { "Is it meant to take this long?", "Still thinking. Probably fine!", "Should I be worried?" },
                new[] { "Phew, it's done!", "It finished! Relief!", "Done! Did it work?" },
                new[] { "It needs you! Hurry-ish!", "Um, a question for you!", "Please don't keep it waiting!" },
                new[] { "Oh good, you're back!", "Phew, moving again.", "Okay! Okay! Going!" },
                new[] { "Are we... okay?", "It's very quiet...", "Did I do something wrong?" },
                new[] { "It ended! Was that planned?", "Bye! Hope it went well!", "Gone... everything okay?" },
                new[] { "I changed! Is that normal?", "New stage! Eep!", "I grew?! Don't panic!" },
            },
            [BuddyPersonality.Zen] = new[]
            {
                new[] { "A new path begins.", "Breathe in. Begin.", "Welcome, session." },
                new[] { "Let the thoughts flow.", "Patience is a feature.", "The answer is ripening." },
                new[] { "It is done.", "The task rests.", "What was asked is answered." },
                new[] { "A choice awaits you.", "The session seeks you.", "Answer when ready." },
                new[] { "And so it flows on.", "The river moves again.", "Onward, gently." },
                new[] { "Stillness is fine too.", "Rest is part of work.", "Silence. Peace." },
                new[] { "All things end. Well done.", "The session rests now.", "Gently, it closes." },
                new[] { "I have grown, as all things do.", "A new form. Same spirit.", "Change is the way." },
            },
            [BuddyPersonality.Chaotic] = new[]
            {
                new[] { "NEW SESSION! Let's gooo!", "Chaos mode: engaged.", "Buckle up!" },
                new[] { "Brain goes BRRRR.", "Thinking at max volume!", "Spinning plates..." },
                new[] { "BOOM. Done!", "It worked?! Wild.", "Done! Nothing exploded!" },
                new[] { "HUMAN! It needs you!", "Press something! Anything!", "Your turn! Go go go!" },
                new[] { "Here we GO!", "Wheee, back in action!", "Yes! More chaos!" },
                new[] { "Too quiet. Suspicious.", "Let's rename every variable.", "Bored. Poking things." },
                new[] { "Session go bye-bye!", "Poof! Gone!", "It's over! Or is it?" },
                new[] { "EVOLUTION! Wild!", "I'm bigger now! Watch out!", "Mutation complete!" },
            },
        };

        // One extra line per moment for the rarer buddies, same moment order.
        // Common and uncommon have none — rarity should be something you
        // notice now and then, not in every bubble.
        private static readonly Dictionary<BuddyRarity, string[]> Flavour = new()
        {
            [BuddyRarity.Rare] = new[]
            {
                "Sparkles on! Let's begin.", "Sparkly thoughts incoming...", "Done, with a little sparkle.",
                "A sparkly nudge: your turn.", "Sparkle, sparkle, back to work.", "Polishing my sparkles.",
                "One last sparkle. Bye!", "Extra sparkly now!",
            },
            [BuddyRarity.Epic] = new[]
            {
                "An epic quest begins!", "Epic thoughts take time.", "An epic victory!",
                "The quest needs its hero!", "The hero returns!", "The hero rests.",
                "Thus ends the epic.", "Epic evolution unlocked!",
            },
            [BuddyRarity.Legendary] = new[]
            {
                "Legends begin like this.", "Legendary brain at work.", "Legendary. Truly.",
                "Legends need your call.", "The legend continues!", "Even legends nap.",
                "Another tale for the legend.", "A legend grows stronger!",
            },
        };
    }
}
