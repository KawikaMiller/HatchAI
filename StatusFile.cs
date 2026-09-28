using System.Text.Json.Serialization;

namespace HatchAI
{
    // One hook-written status file, as HatchAI reads it: the fields the buddy
    // and StatusReader's rules use, and nothing else.
    //
    // The file is Claude Buddy's contract, written by ClaudeBuddyHook.sh and
    // ClaudeBuddyHook.ps1, and the property names below are the hooks' own.
    // Claude Buddy's SessionStatus carries this and thirty app-derived fields
    // beside it (team, presence, kind, colour and so on); none of those is on
    // disk, and none is needed to decide which sessions exist and what state
    // they are in. Fields the hooks write that are not listed here — color,
    // term_id's siblings tmux_socket and tmux_bin — are simply ignored by the
    // deserializer.
    //
    // Read-only by construction: nothing in HatchAI ever serializes one of
    // these back, because nothing in HatchAI ever writes to the status folder.
    internal sealed class StatusFile
    {
        // idle, generating or waiting. ("ended" is never written: the hook
        // deletes the file instead, and a session ends by disappearing.)
        [JsonPropertyName("state")] public string State { get; set; } = "idle";

        // Which CLI wrote the file, in its own words: "codex", "grok", or
        // absent for Claude Code. See StatusReader.SourceOf.
        [JsonPropertyName("cli")] public string Cli { get; set; } = "";

        [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";

        [JsonPropertyName("title")] public string Title { get; set; } = "";

        [JsonPropertyName("transcript_path")] public string TranscriptPath { get; set; } = "";

        // The CLI process's pid; 0 from a hook older than the field. Used for
        // the dead-process rule and to group ids one process moved on from.
        [JsonPropertyName("session_pid")] public int SessionPid { get; set; }

        // Terminal coordinates, used only by KnowsATerminal and the
        // no-terminal rule. The bash hook writes tty and tmux_pane, the
        // PowerShell hook writes term_pid instead; both write term_program.
        [JsonPropertyName("term_program")] public string TermProgram { get; set; } = "";
        [JsonPropertyName("term_id")] public string TermId { get; set; } = "";
        [JsonPropertyName("tty")] public string Tty { get; set; } = "";
        [JsonPropertyName("tmux_pane")] public string TmuxPane { get; set; } = "";
        [JsonPropertyName("term_pid")] public int TermPid { get; set; }

        // Derived from Cli by StatusReader.SourceOf when the file is read, and
        // never on disk.
        [JsonIgnore] public SessionSource Source { get; set; } = SessionSource.ClaudeCode;

        // Whether this is a CLI running on this machine. Always true for a
        // status file today — the three CLIs a hook can name are all local —
        // and kept as a property so the rules copied from Claude Buddy read
        // exactly as they do there.
        [JsonIgnore]
        public bool IsLocalCli => Source is SessionSource.ClaudeCode or SessionSource.Codex or SessionSource.Grok;
    }
}
