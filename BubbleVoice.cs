namespace HatchAI
{
    // CB-202: the buddy's generated speech bubbles, as the contracts four
    // engineers build against in parallel (see docs/buddy-design.md, "AI speech
    // bubbles"). This file is the shared vocabulary: what one bubble asks for,
    // the one interface the controller talks to, and the two constants that
    // more than one owner has to agree on. The text rules live in
    // BubbleText.cs and the process in ClaudeCliBubbleGenerator.cs.

    // One bubble's worth of context: everything a generated line may be based
    // on, and — Moment through Prompt — the whole of what may leave the machine
    // (the owner-approved list on CB-202, plus Species since the owner's
    // voice-mix request there: buddy data rather than user data, and named in
    // the disclosure like the rest). Stats joined the same way, on the owner's
    // later request to send all five numbers rather than a summary of them:
    // the genome's own, never anything earned or typed. It sits beside
    // Species and is positional, not defaulted, so a caller that forgets it
    // fails to compile rather than quietly sending five zeros. Project is a
    // folder *name*, never a path.
    // Prompt, once set, is already trimmed and redacted (BubblePromptRedactor);
    // BubblePrompt.User is the only thing that turns a request into sent text,
    // and a test pins exactly what it contains.
    //
    // TranscriptPath and Source are how the prompt is found, not what is sent.
    // The controller runs on the UI thread and must not read a transcript
    // there, so it hands over where the prompt lives and the generator reads,
    // trims and redacts it off-thread, filling Prompt before building the
    // text. BubblePrompt.User must never read either field.
    internal sealed record BubbleRequest(
        BuddyMoment Moment,
        BuddyPersonality Primary,
        BuddyPersonality Secondary,
        BuddyRarity Rarity,
        BuddySpecies Species,
        BuddyStats Stats,
        string? Project,
        string? Prompt,
        string? TranscriptPath = null,
        SessionSource Source = SessionSource.ClaudeCode);

    // What the controller asks for a line. Null means "use the table"
    // (BubbleLines.Pick) — every failure a generator can have answers null
    // rather than throwing, so the controller has one fallback arm, not one per
    // failure. Cancellation is how the controller says the moment went stale or
    // timed out; a cancelled call's answer is dropped whatever it is, and an
    // implementation that starts a process must kill it when `ct` fires.
    internal interface IBubbleGenerator
    {
        Task<string?> GenerateAsync(BubbleRequest request, CancellationToken ct);
    }

    internal static class BubbleVoice
    {
        // The leaf of the folder every generation call runs in. Claude Code
        // names a project folder after its cwd with separators turned into
        // dashes, so anything that call might ever write lands in a folder
        // ending "-claudebuddy-bubble-voice" — which BuddyLedgerScanner skips,
        // so the buddy never counts its own voice (CB-202 plan, risk 1).
        //
        // **Never rename this, even though the app is HatchAI now.** It is a
        // contract between two apps: Claude Buddy's status scan drops any
        // session whose cwd has this leaf, which is how HatchAI's generation
        // calls stay invisible there, and HatchAI's StatusReader drops Claude
        // Buddy's own spoken-summary `claude -p` the same way, since that
        // runs from this folder with hooks enabled. Both apps must spell it
        // byte for byte the same.
        internal const string WorkDirLeaf = "claudebuddy-bubble-voice";

        // How long a bubble may wait for its line before the table answers
        // instead. Measured wall time was 2.7-2.8 s with thinking off (one warm
        // machine, one quiet five-minute window — not a cold start or a busy
        // API). Checked in BuddyController.Pump against the injected clock,
        // not by a timer inside the generator.
        internal static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(6);

        // The folder itself: under temp, which is where SpeechSummary already
        // runs for the same reason — no project's CLAUDE.md sits at or above
        // it. Created by the generator before use, not here.
        internal static string WorkDir => Path.Combine(Path.GetTempPath(), WorkDirLeaf);

        // True for the voice's own working directory and for the Claude Code
        // project folder named after it: a path whose last segment is
        // WorkDirLeaf, or ends with "-" + WorkDirLeaf, case-insensitively,
        // trailing separators ignored. Used by BuddyLedgerScanner.Discover and
        // by StatusReader's filter (and by Claude Buddy's own scan). Owner: E2.
        //
        // Both separators on every platform: a status file's cwd can come from
        // WSL or a peer, and a Claude Code project folder name contains
        // neither, so accepting both costs nothing.
        //
        // The dash-suffix form is what makes this work against Claude Code's
        // real folder names, which it derives from the cwd by turning every
        // character that is not a letter or digit into '-' — measured on the
        // Windows dev machine as `C--Users-<name>-AppData-Local-Temp` for the
        // temp folder. TranscriptReader.EncodeCwd does not reproduce that on
        // Windows (it replaces only the separator and keeps the drive colon),
        // so this deliberately does not encode anything: it keys on the leaf,
        // which is letters, digits and dashes and survives the encoding intact.
        internal static bool IsOwnWorkDir(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            var trimmed = path.TrimEnd('/', '\\');
            var leaf = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];

            return leaf.Equals(WorkDirLeaf, StringComparison.OrdinalIgnoreCase)
                || leaf.EndsWith("-" + WorkDirLeaf, StringComparison.OrdinalIgnoreCase);
        }
    }
}
