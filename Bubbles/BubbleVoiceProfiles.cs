namespace HatchAI
{
    // CB-202's voice mix: one short description per personality and per
    // species, which BubblePrompt.User puts beside the bare word so the model
    // is told what "Anxious" or "axolotl" is meant to sound like rather than
    // guessing. Only the buddy's own two personalities and one species are
    // ever sent; the rest of the table stays on the machine.
    //
    // Plain ASCII with none of & | < > ^ % and no double quote, the same rule
    // as BubblePrompt.System, and pinned by a test. These go over stdin rather
    // than the command line, so cmd.exe never re-parses them today; the rule
    // is kept anyway so moving a description into the system prompt later is
    // not a quoting bug waiting to happen.
    //
    // The personality lines are written to the tone of BubbleLines' table, so
    // a generated line and a table line from the same buddy sound like the
    // same pet. The species lines are flavour only: a light hint at word
    // choice, not an invitation to pun, which the system prompt says too.
    //
    // Measured on Haiku (CB-202, 12 calls with this prompt): the personality
    // blend comes through clearly, the species only faintly. A duck quacked in
    // two of five lines despite "no forced puns"; a robot showed nothing with
    // "beeps and computes" (0 of 3) and one faint "logic loop" with the
    // current wording (1 of 3). Robot is the only entry reworded against the
    // model, and n=3 cannot really tell those two wordings apart; the other
    // sixteen species lines are unmeasured. Species is the weaker dial.
    //
    // A switch rather than a dictionary, so an enum value appended without a
    // description throws on its first bubble — which the generator turns into
    // the table line — and fails EveryPersonalityHasAVoice / EverySpeciesHasAVoice
    // long before that.
    internal static class BubbleVoiceProfiles
    {
        internal static string Of(BuddyPersonality personality) => personality switch
        {
            BuddyPersonality.Cheerful => "bubbly, upbeat, cheers the owner on",
            BuddyPersonality.Grumpy => "gruff, grudging, dry complaints but secretly helpful",
            BuddyPersonality.Sleepy => "drowsy, yawning, slow and cosy",
            BuddyPersonality.Curious => "nosy, full of questions, easily fascinated",
            BuddyPersonality.Sassy => "witty, dramatic, playfully smug",
            BuddyPersonality.Anxious => "worried, fussy, hopes everything is fine",
            BuddyPersonality.Zen => "calm, gentle, speaks in quiet wisdom",
            BuddyPersonality.Chaotic => "loud, gleeful, mischievous, loves a mess",
            _ => throw new ArgumentOutOfRangeException(nameof(personality), personality, "no voice for this personality"),
        };

        internal static string Of(BuddySpecies species) => species switch
        {
            BuddySpecies.Duck => "chatty little pond bird",
            BuddySpecies.Goose => "bold, honking, territorial bird",
            BuddySpecies.Owl => "bookish night bird, a little wise",
            BuddySpecies.Penguin => "tidy, formal little bird from the cold",
            BuddySpecies.Cat => "aloof, easily distracted, likes warm spots",
            BuddySpecies.Rabbit => "quick, twitchy, bouncy little critter",
            BuddySpecies.Capybara => "calm, friendly, gets along with everyone",
            BuddySpecies.Chonk => "round, snack-minded, happily heavy critter",
            BuddySpecies.Octopus => "clever sea creature that juggles many things",
            BuddySpecies.Axolotl => "smiley, unbothered water critter",
            BuddySpecies.Turtle => "slow, steady, patient shell dweller",
            BuddySpecies.Snail => "slow, soft-spoken, carries its home along",
            BuddySpecies.Dragon => "proud, fiery little treasure hoarder",
            BuddySpecies.Ghost => "floaty, whispery, friendly little spook",
            BuddySpecies.Cactus => "prickly outside, soft inside, loves sunshine",
            BuddySpecies.Mushroom => "earthy, quiet, grows in damp dark corners",
            BuddySpecies.Robot => "precise, literal, talks in tidy machine terms",
            BuddySpecies.Blob => "squishy, wobbly, goes with the flow",
            _ => throw new ArgumentOutOfRangeException(nameof(species), species, "no voice for this species"),
        };
    }
}
