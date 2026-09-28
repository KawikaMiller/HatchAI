namespace HatchAI
{
    // Which kind of session a status file describes. Extracted verbatim from
    // Claude Buddy's SessionManager.cs, all six members, in the same order.
    //
    // HatchAI's StatusReader only ever produces ClaudeCode, Codex or Grok — a
    // status file's `cli` field can say nothing else — but the other three are
    // kept rather than trimmed: the ported LatestUserPrompt tests name
    // OpenClaw, and an enum whose order silently differs between two apps that
    // share a status folder is a trap nobody would look for.
    //
    // The sessions differ in what their transcripts look like and in what
    // they can be asked to do, not in whether there is a terminal behind them.
    public enum SessionSource
    {
        ClaudeCode,
        Codex,
        Grok,
        OpenClaw,

        // A Claude Code session on another machine, seen through a bridge.
        // Never produced by HatchAI.
        RemoteControl,

        // A Claude Code session running in Anthropic's cloud, listed by the
        // account API rather than by a hook. Never produced by HatchAI.
        ClaudeCloud
    }
}
