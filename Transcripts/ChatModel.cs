namespace HatchAI
{
    // The two chat types the transcript parsers produce, and nothing more.
    //
    // In Claude Buddy these live in RemoteChat.cs beside a chat panel, a
    // gateway and a mirror, and ChatTurn notifies property changes because a
    // streaming reply mutates it in place under a bound list. HatchAI has no
    // chat panel: the only reader of a turn is the buddy's AI-bubble prompt,
    // which takes the latest user turn once and throws the rest away. So this
    // keeps exactly the members ChatTranscript and CodexTranscript set, as
    // plain properties, and drops the notification, the media-confidence tier
    // and the OpenClaw fields that had no producer here.
    //
    // Enum order matches Claude Buddy's, in case a value is ever persisted or
    // compared across the two apps.
    public enum ChatRole { User, Assistant, System }

    public sealed class ChatTurn
    {
        public ChatRole Role { get; init; }

        public string Text { get; set; } = "";

        public bool IsComplete { get; set; }

        // When this turn happened, in local time; defaulted to now, and
        // overwritten from the transcript's own timestamp by the parsers.
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

        // A picture a local CLI's transcript carried inline as base64. The
        // buddy never shows it; it is kept because the parsers set it and the
        // ported ChatTranscriptEdge tests assert on it.
        public byte[]? ImageBytes { get; set; }
    }
}
