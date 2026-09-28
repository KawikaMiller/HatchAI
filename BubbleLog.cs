using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HatchAI
{
    // CB-202, after live use: the buddy spoke while the owner's conversation
    // sat idle, and nothing could say which moment it was or whether the line
    // was generated. This file is the answer — a local, append-only record of
    // every bubble decision the controller makes, one JSON object per line.
    //
    // **It records the text of the bubble, by the owner's choice**, so a
    // generated line that echoes what the user was working on ends up in it.
    // That is why it stays on this machine, why Settings says what it holds and
    // where, and why it has its own off switch (buddyBubbleLogEnabled). What
    // it never holds: the user's prompt, the request, the system prompt, or
    // anything the CLI printed. The only generated text in it is the validated
    // line that was actually shown.

    // What became of one decision. Exactly one entry per decision.
    internal enum BubbleOutcome
    {
        ShownTable,
        ShownAi,
        // A line was asked for and the table spoke instead; Reason says why.
        Fallback,
        // Stale: the moment passed before anything was said; nothing shown.
        Dropped,
        // Final no: idle bubbles off, bubbles off, the moment's own cooldown,
        // or a held moment outranking this one.
        Suppressed,
        // The global gap said "not yet". Logged once, when first held; what
        // happens to it later is its own entry.
        Deferred,
    }

    // Why a generated line did not come back, as far as the generator can
    // tell. The contract (IBubbleGenerator.GenerateAsync) answers null for all
    // of these on purpose — the controller has one fallback arm — so the
    // reason travels separately, through IBubbleGeneratorDiagnostics, and only
    // ever into the log.
    internal enum BubbleFailure
    {
        // No `claude` binary found.
        NoCli,
        // The process could not be started.
        StartFailed,
        // It ran and exited non-zero.
        NonZeroExit,
        // It ran and the exchange broke some other way (stdin closed under
        // it), or a `run` seam answered null without saying why.
        ProcessFailed,
        // stdout was not the JSON object with a `result` the parser expects.
        BadOutput,
        // A line came back and BubbleLineValidator refused it.
        RejectedByValidator,
        // Something threw that the generator did not anticipate.
        Threw,
        // The token fired: the controller gave up on it first.
        Cancelled,
        // Controller-side: the 6 s GenerationTimeout passed with no answer.
        TimedOut,
        // Controller-side: the generator answered null and offers no reason.
        Unknown,
    }

    // Optional, beside IBubbleGenerator rather than in it: the contract four
    // engineers built against stays exactly as it was, and a generator that
    // cannot say why it failed (a fake, a future one) is still a generator.
    // The controller reads this right after an answer comes back, and only if
    // the generator implements it. Single flight is what makes one field
    // enough — there is never a second call for it to be about.
    internal interface IBubbleGeneratorDiagnostics
    {
        BubbleFailure? LastFailure { get; }
    }

    internal static class BubbleLogReason
    {
        internal const string IdleOff = "idle-off";
        internal const string BubblesOff = "bubbles-off";
        internal const string Cooldown = "cooldown";
        internal const string Outranked = "outranked";
        internal const string FocusMoved = "focus-moved";
        internal const string StateChanged = "state-changed";
        internal const string BuddyHidden = "buddy-hidden";
        internal const string Superseded = "superseded";
        internal const string Disposed = "disposed";
        internal const string AiOff = "ai-off";

        internal static string Of(BubbleFailure failure) => failure switch
        {
            BubbleFailure.NoCli => "no-cli",
            BubbleFailure.StartFailed => "start-failed",
            BubbleFailure.NonZeroExit => "non-zero-exit",
            BubbleFailure.ProcessFailed => "process-failed",
            BubbleFailure.BadOutput => "bad-output",
            BubbleFailure.RejectedByValidator => "rejected-by-validator",
            BubbleFailure.Threw => "threw",
            BubbleFailure.Cancelled => "cancelled",
            BubbleFailure.TimedOut => "timed-out",
            _ => "unknown",
        };
    }

    // One decision. Pure data; ToJsonLine is the whole of the format.
    //
    // Source is the voice that spoke or was about to — "table" or "ai" — and
    // null when no voice was chosen (suppressed, deferred, or a held moment
    // dropped before it was asked for). LatencyMs is set only when a line was
    // asked for: from the ask to the outcome, on the controller's clock.
    // AiEnabled is whether AI bubbles was on (and a voice wired) at the time.
    internal sealed record BubbleLogEntry(
        DateTimeOffset At,
        BuddyMoment Moment,
        BubbleOutcome Outcome,
        string? Reason,
        string? Source,
        long? LatencyMs,
        string? Text,
        bool AiEnabled)
    {
        internal const string TableSource = "table";
        internal const string AiSource = "ai";

        // Relaxed escaping keeps a line readable in a text editor (an
        // apostrophe stays an apostrophe), and still escapes quotes, control
        // characters and line breaks — which is what keeps one entry on one
        // line whatever the model said. The file is never served as HTML,
        // which is the only thing "unsafe" in the name is about.
        private static readonly JsonWriterOptions Options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        internal static string OutcomeName(BubbleOutcome outcome) => outcome switch
        {
            BubbleOutcome.ShownTable => "shown-table",
            BubbleOutcome.ShownAi => "shown-ai",
            BubbleOutcome.Fallback => "fallback",
            BubbleOutcome.Dropped => "dropped",
            BubbleOutcome.Suppressed => "suppressed",
            _ => "deferred",
        };

        // Every key on every line, null or not, so a line can be read — or
        // grepped — without knowing which outcome it is first.
        internal string ToJsonLine()
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, Options))
            {
                json.WriteStartObject();
                json.WriteString("ts", At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                json.WriteString("moment", Moment.ToString());
                json.WriteString("outcome", OutcomeName(Outcome));
                json.WriteString("reason", Reason);
                json.WriteString("source", Source);
                if (LatencyMs is { } ms) json.WriteNumber("latencyMs", ms);
                else json.WriteNull("latencyMs");
                json.WriteString("text", Text);
                json.WriteBoolean("aiEnabled", AiEnabled);
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    // Where decisions go. Log must never throw and never block on a disk.
    internal interface IBubbleLog
    {
        void Log(BubbleLogEntry entry);
    }

    // The production log: bubble-log.jsonl beside settings.json, rolled over
    // to bubble-log.1.jsonl at about 512 KB so the pair stays near a megabyte.
    //
    // The controller calls Log on the UI thread, and a write can stall on
    // anything from antivirus to a network home folder, so the file work is
    // done by one background drain over a queue. Only one drain runs at a
    // time, and every write happens under _writeLock with its batch taken
    // inside that lock, so lines land in the order they were logged. Dispose
    // (on quit) writes whatever is still queued on the calling thread.
    //
    // Like CrashLog, a log that throws is worse than none: a locked file, a
    // read-only folder or a full disk lose the entry and nothing else. Nothing
    // is reported about the failure either — there is nowhere to report it
    // that would not be noise, and the next entry simply tries again.
    internal sealed class BubbleLogFile : IBubbleLog, IDisposable
    {
        internal const string FileName = "bubble-log.jsonl";
        internal const string PreviousFileName = "bubble-log.1.jsonl";
        internal const long DefaultMaxBytes = 512 * 1024;

        // Entries waiting on a stalled disk. Past this they are dropped rather
        // than held: a bounded file is the promise, and a bounded queue is
        // what keeps it when the disk is the problem.
        internal const int DefaultMaxQueued = 1000;

        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _directory;
        private readonly long _maxBytes;
        private readonly int _maxQueued;
        private readonly Action<Action> _schedule;
        private readonly object _gate = new();
        private readonly object _writeLock = new();
        private readonly Queue<string> _queue = new();
        private bool _draining;
        private bool _disposed;

        // Next to settings.json, honouring CLAUDE_BUDDY_SETTINGS_DIR through
        // HatchAISettings.Directory itself, so every test that isolates
        // settings isolates this too.
        internal static string DefaultDirectory => HatchAISettings.Directory;

        internal static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

        // `schedule` is how a drain is started: Task.Run in production, and
        // `a => a()` for a test that wants every Log written before it
        // returns — the synchronous seam. A test can also hold the action to
        // stand in for a stalled disk.
        internal BubbleLogFile(
            string? directory = null,
            long maxBytes = DefaultMaxBytes,
            int maxQueued = DefaultMaxQueued,
            Action<Action>? schedule = null)
        {
            _directory = directory ?? DefaultDirectory;
            _maxBytes = maxBytes;
            _maxQueued = maxQueued;
            _schedule = schedule ?? (drain => Task.Run(drain));
        }

        internal string Path_ => Path.Combine(_directory, FileName);
        internal string PreviousPath => Path.Combine(_directory, PreviousFileName);

        public void Log(BubbleLogEntry entry)
        {
            // Serialised here rather than in the drain, so the queue holds
            // strings and not entries. Not wrapped: the writer replaces even
            // invalid UTF-16 rather than throwing (BubbleLogTests pins that),
            // and the controller's own Record catches everything regardless.
            var line = entry.ToJsonLine();

            lock (_gate)
            {
                if (_disposed || _queue.Count >= _maxQueued) return;
                _queue.Enqueue(line);
                if (_draining) return;
                _draining = true;
            }

            try { _schedule(Drain); }
            catch
            {
                // Nothing could be scheduled; the entry waits for Dispose.
                lock (_gate) _draining = false;
            }
        }

        private void Drain()
        {
            while (WriteBatch(endsDrain: true)) { }
        }

        // Takes what is queued and writes it, all under _writeLock so batches
        // cannot overtake each other. False once there was nothing to take;
        // for the drain, that is also the moment it stops being the drain.
        private bool WriteBatch(bool endsDrain)
        {
            lock (_writeLock)
            {
                string[] batch;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        if (endsDrain) _draining = false;
                        return false;
                    }
                    batch = _queue.ToArray();
                    _queue.Clear();
                }

                Write(batch);
                return true;
            }
        }

        private void Write(IReadOnlyList<string> lines)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                FileStream? stream = null;
                try
                {
                    foreach (var line in lines)
                    {
                        stream ??= Open();

                        // Checked per line, so one large batch cannot carry
                        // the file far past the limit.
                        if (stream.Length >= _maxBytes)
                        {
                            stream.Dispose();
                            File.Move(Path_, PreviousPath, overwrite: true);
                            stream = Open();
                        }

                        stream.Write(Utf8.GetBytes(line + "\n"));
                    }
                }
                finally
                {
                    stream?.Dispose();
                }
            }
            catch
            {
                // Locked, read-only, full, or a folder deleted underneath:
                // what was in this batch is lost, and the buddy is not.
            }
        }

        // FileShare.ReadWrite so the user can have the file open while it
        // grows; Delete so their viewer does not block the next rollover.
        private FileStream Open() =>
            new(Path_, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        // Writes whatever is still queued, on the calling thread.
        internal void Flush()
        {
            while (WriteBatch(endsDrain: false)) { }
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            Flush();
        }
    }
}
