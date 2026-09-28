using System.Diagnostics;
using System.Text;

namespace HatchAI
{
    // CB-202's production generator: one headless `claude -p` per bubble,
    // reusing the user's own login. See docs/buddy-design.md for the
    // invocation and the decisions behind it.
    //
    // Every input that touches the machine is a seam, so the class can be
    // driven end to end in a test without a CLI, a transcript or a billed call:
    //
    //   locate          — the claude binary, or null when there is none
    //                     (production: ClaudeBinary.Locate, uncached — see the
    //                     constructor). Null answers null.
    //   run             — spawn `startInfo`, write `stdin`, return stdout, or
    //                     null on a failed start or a non-zero exit. Must
    //                     InternalSessions.Remember the pid before writing and
    //                     Forget it in a finally, and kill the process when the
    //                     token fires.
    //   transcriptLines — a transcript's tail (production:
    //                     TranscriptReader.TailLines).
    //   clock           — for anything the generator itself has to time. Nothing
    //                     does today: the 6 s bubble timeout is the controller's,
    //                     and it reaches this class only as `ct` firing.
    //
    // **The contract is "null or a validated line, never an exception."** The
    // controller has exactly one fallback arm, the table, and a generator that
    // threw would need a second one — or, on the UI thread, would take the buddy
    // with it. So GenerateAsync catches everything, including the
    // NotImplementedException a text-layer stub throws, and every arm answers
    // the same way.
    //
    // Nothing the model says and nothing the user typed is logged, on any path.
    // Both are the user's data — one untrusted, one sensitive — and the only
    // thing a log line could add is a place for either to leak to. The one
    // exception is outside this class and deliberate: the bubble log
    // (BubbleLog.cs) records the validated line the user was shown, by the
    // owner's choice, and gets from here only a failure category.
    internal sealed class ClaudeCliBubbleGenerator : IBubbleGenerator
    {
        // "About 200 characters" (CB-202's approved scope), applied after
        // redaction so a mask never gets cut in half into something that is no
        // longer recognisably a mask.
        internal const int MaxPromptChars = 200;

        private readonly Func<string?> _locate;
        private readonly Func<ProcessStartInfo, string, CancellationToken, Task<string?>> _run;
        private readonly Func<string, IEnumerable<string>> _transcriptLines;
        private readonly Func<DateTimeOffset> _clock;

        // Why the latest call answered null, for the bubble log (CB-202). A
        // category, never a message: an exception's text or the CLI's output
        // could carry the user's prompt, and the class header's rule holds
        // for the log as much as anywhere. Reset at the start of every call;
        // there is only ever one in flight, so it cannot describe another
        // call's failure. An int behind Volatile because the call finishes on
        // a pool thread and the controller reads it on the UI thread, and a
        // nullable enum cannot be volatile. None is "no failure".
        private const int None = -1;
        private int _lastFailure = None;

        // What the production `run` seam says about its own null, which its
        // return type cannot carry. None when the seam said nothing — an
        // injected one never does.
        private int _runFailure = None;

        public BubbleFailure? LastFailure =>
            Volatile.Read(ref _lastFailure) is var f && f != None ? (BubbleFailure)f : null;

        private void Fail(BubbleFailure failure) => Volatile.Write(ref _lastFailure, (int)failure);

        internal ClaudeCliBubbleGenerator(
            Func<string?>? locate = null,
            Func<ProcessStartInfo, string, CancellationToken, Task<string?>>? run = null,
            Func<string, IEnumerable<string>>? transcriptLines = null,
            Func<DateTimeOffset>? clock = null)
        {
            // ClaudeBinary.Locate rather than ClaudeBinary.Path, deliberately.
            // Path answers once per process and then forever: a user who turns
            // AI bubbles on, sees them fall back to the table, installs the CLI
            // and carries on would otherwise keep getting the table until they
            // restarted the app, with nothing to say why. Locate is a handful of
            // File.Exists calls, run off the UI thread, at most once per bubble
            // — and bubbles are already rate-limited to one per 20 s.
            _locate = locate ?? (() => ClaudeBinary.Locate());
            _run = run ?? ((startInfo, stdin, ct) => RunProcessAsync(
                startInfo, stdin, ct, failure => Volatile.Write(ref _runFailure, (int)failure)));
            _transcriptLines = transcriptLines ?? TranscriptReader.TailLines;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public async Task<string?> GenerateAsync(BubbleRequest request, CancellationToken ct)
        {
            Volatile.Write(ref _lastFailure, None);
            Volatile.Write(ref _runFailure, None);
            try
            {
                // Task.Run: the controller calls this on the UI thread, and
                // everything below — the File.Exists walk, the transcript read,
                // the process — must not be.
                return await Task.Run(() => GenerateOffThreadAsync(request, ct), ct).ConfigureAwait(false);
            }
            catch
            {
                // Cancellation, a stub, a seam that threw, a transcript in a
                // shape nobody anticipated: all of them are "use the table".
                // The exception is dropped rather than logged because its
                // message can carry a path or a fragment of the text it failed
                // on — see the class header. Only its category is kept.
                Fail(ct.IsCancellationRequested ? BubbleFailure.Cancelled : BubbleFailure.Threw);
                return null;
            }
        }

        private async Task<string?> GenerateOffThreadAsync(BubbleRequest request, CancellationToken ct)
        {
            var claude = _locate();
            if (claude is null)
            {
                Fail(BubbleFailure.NoCli);
                return null;
            }

            var sent = request with { Prompt = request.Prompt ?? ReadPrompt(request) };
            var user = BubblePrompt.User(sent);

            var stdout = await _run(StartInfoFor(claude, BubbleVoice.WorkDir), user, ct).ConfigureAwait(false);

            // A late answer from a call the controller has already given up on
            // is dropped here as well as there, so a line can never reach the
            // controller for a moment it has cancelled.
            if (ct.IsCancellationRequested)
            {
                Fail(BubbleFailure.Cancelled);
                return null;
            }

            if (stdout is null)
            {
                var said = Volatile.Read(ref _runFailure);
                Fail(said != None ? (BubbleFailure)said : BubbleFailure.ProcessFailed);
                return null;
            }

            // Parsed and validated as two steps rather than one call, only so
            // the log can tell "not the JSON we expected" from "a line the
            // validator refused". The rules themselves are BubbleText's.
            var parsed = BubbleCliOutput.Parse(stdout);
            if (parsed is null)
            {
                Fail(BubbleFailure.BadOutput);
                return null;
            }

            var line = BubbleLineValidator.Validate(parsed);
            if (line is null) Fail(BubbleFailure.RejectedByValidator);
            return line;
        }

        // The user's latest prompt, ready to send: read from the transcript's
        // tail, redacted, then trimmed. Null when there is no transcript, no
        // user turn in its tail, or nothing left after trimming — the prompt is
        // optional context, and a bubble without it is still a bubble.
        //
        // Only the tail: the latest prompt is by definition near the end, and a
        // multi-megabyte transcript is not read to find it.
        private string? ReadPrompt(BubbleRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TranscriptPath)) return null;

            var raw = LatestUserPrompt.From(_transcriptLines(request.TranscriptPath), request.Source);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var home = HomeForRedaction(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return Trim(BubblePromptRedactor.Redact(raw, home));
        }

        // GetFolderPath answers "" rather than null when it has no answer (a
        // service account, a stripped environment). An empty home prefix would
        // match everywhere — or, handed to string.Replace, throw — so it is
        // passed on as null, which the redactor's contract reads as "skip the
        // home-prefix pass only".
        internal static string? HomeForRedaction(string? home) =>
            string.IsNullOrEmpty(home) ? null : home;

        // Whitespace collapsed first, so the budget is spent on words rather
        // than on the indentation of a pasted block, then cut at the limit.
        // Never splits a surrogate pair: half an emoji is an invalid string,
        // and what the CLI does with one on stdin is not something to find out
        // in production.
        internal static string? Trim(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (collapsed.Length <= MaxPromptChars) return collapsed;

            var cut = MaxPromptChars;
            if (char.IsHighSurrogate(collapsed[cut - 1])) cut--;
            return collapsed[..cut].TrimEnd() + "…";
        }

        // The invocation, as a value, so a test pins every flag without
        // spawning anything (the SpeechSummary.StartInfoFor pattern). Each flag
        // is there because of something measured on CB-202's plan:
        //
        //   -p --model haiku           headless, and the cheapest model; the
        //                              job is one sentence, not reasoning.
        //   --output-format json       one object whose `result` is the line,
        //                              so a stray preamble is not the bubble.
        //   --no-session-persistence   no transcript under ~/.claude/projects.
        //                              Without it the ledger found the call's
        //                              transcript and credited the buddy for
        //                              its own voice (+4,188 tokens, measured).
        //   --tools ""                 no tools: nothing the model says can
        //   --strict-mcp-config        read a file, run a command or reach an
        //   --disable-slash-commands   MCP server, whatever the prompt holds.
        //   --settings {...}           disableAllHooks: hooks fire for `-p`
        //                              (measured), and the user's own
        //                              ClaudeBuddyHook would draw an orb and
        //                              fire a SessionStarted bubble for the
        //                              buddy talking to itself — which would
        //                              ask for another bubble. And
        //                              alwaysThinkingEnabled false: thinking is
        //                              on by default and cost 3,367 output
        //                              tokens and 31.5 s against 29 and 2.8 s
        //                              for the same call.
        //   --system-prompt            the fixed voice; per-bubble text goes
        //                              over stdin.
        //
        // `--bare` would do much of this in one flag and is not usable: it
        // accepts only API-key login, and the point is to reuse the user's own.
        //
        // **The user's prompt is never an argument.** A command line is readable
        // by every other process on the machine (`ps`, Task Manager); stdin is
        // not. The system prompt is ours and fixed, so it may be.
        //
        // On Windows, if ClaudeBinary resolves to an npm `claude.cmd` shim
        // rather than claude.exe, CreateProcess runs it through cmd.exe, which
        // re-parses this command line: `%`, `^`, `&`, `|`, `<` and `>` in the
        // system prompt could then be interpreted rather than passed.
        // ArgumentList's quoting protects the JSON and the empty `--tools`
        // value from the C runtime's parser, not from cmd's. Measured only
        // against claude.exe (the native installer's); the shim path is
        // ASSUMED, see docs/buddy-design.md.
        //
        // Creates `workDir` if it is missing, because a WorkingDirectory that
        // does not exist fails the start with an error that names neither the
        // folder nor the reason.
        internal static ProcessStartInfo StartInfoFor(string claudePath, string workDir) =>
            StartInfoFor(claudePath, workDir, BubblePrompt.System);

        // The same with the system prompt as a parameter, so the invocation can
        // be pinned — and driven against a fake CLI — independently of the
        // text layer's wording, which is a different owner's and changes for
        // different reasons.
        internal static ProcessStartInfo StartInfoFor(string claudePath, string workDir, string systemPrompt)
        {
            Directory.CreateDirectory(workDir);

            // No BOM on any stream. A BOM on stdin arrives as the first
            // character of the user message; on stdout it would sit in front of
            // the JSON the parser expects to start with '{'.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            var startInfo = new ProcessStartInfo
            {
                FileName = claudePath,
                WorkingDirectory = workDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = utf8,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in Arguments(systemPrompt))
                startInfo.ArgumentList.Add(argument);

            return startInfo;
        }

        // The argument list on its own, so the flags can be pinned by a test
        // that does not depend on what the system prompt's text is.
        internal static IReadOnlyList<string> Arguments(string systemPrompt) =>
        [
            "-p",
            "--model", "haiku",
            "--output-format", "json",
            "--no-session-persistence",
            "--tools", "",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--settings", Settings,
            "--system-prompt", systemPrompt,
        ];

        // A literal rather than serialised, so the test compares exactly the
        // bytes the CLI receives.
        internal const string Settings = "{\"disableAllHooks\":true,\"alwaysThinkingEnabled\":false}";

        // The production `run` seam. Process plumbing only; answers null for
        // every failure and never throws.
        //
        // Unlike SpeechSummary.RunAsync this is not excluded from coverage: it
        // takes the start info as a parameter, so the integration suite drives
        // it with a fake `claude` script and nothing is billed.
        //
        // `report` hears which failure a null was (CB-202's bubble log): a
        // start that failed, a non-zero exit, or an exchange that broke with
        // the token still unfired. Cancellation is not reported; the caller
        // knows its own token. A category only — nothing the process printed.
        internal static async Task<string?> RunProcessAsync(
            ProcessStartInfo startInfo, string stdin, CancellationToken ct, Action<BubbleFailure>? report = null)
        {
            if (ct.IsCancellationRequested) return null;

            Process? proc = null;
            try
            {
                proc = new Process { StartInfo = startInfo };

                // Throws for a missing binary (Win32Exception) rather than
                // answering false. With UseShellExecute off, false would mean
                // "reused an existing process", which cannot happen here.
                proc.Start();

                var pid = proc.Id;

                // Before the first byte goes in: the hook that writes this
                // child's status file fires on its own schedule, and the scan
                // runs on a timer. disableAllHooks should mean there is no
                // status file to hide; this is the guard for the day there is.
                //
                // In-process only. HatchAI is its own app now, so this claim
                // hides the call from HatchAI's own StatusReader and from
                // nothing else: Claude Buddy, reading the same status folder,
                // cannot see this set. What protects Claude Buddy is, first,
                // disableAllHooks above — no hook runs, so no status file
                // exists for anyone to draw — and second, the cwd: a Claude
                // Buddy build that carries the BubbleVoice.IsOwnWorkDir scan
                // guard drops any session in a folder whose leaf is
                // WorkDirLeaf, which is why that constant is identical in both
                // apps and must stay so. A released Claude Buddy without that
                // guard rests on disableAllHooks alone.
                //
                // When the binary is a .cmd shim on Windows, `pid` is cmd.exe's
                // and the claude process under it has another, so this claim
                // hides nothing even here; StatusReader's own IsOwnWorkDir
                // rule is the layer that holds then.
                InternalSessions.Remember(pid);
                try
                {
                    // Both pipes drained before anything waits, and stderr read
                    // even though it is discarded: an undrained pipe can
                    // deadlock a chatty child (the hazard BackgroundJobs.ReadOne
                    // and UsagePoller document). Neither read is bound to `ct` —
                    // they end when the process does, which the kill below
                    // guarantees.
                    var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
                    var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

                    await proc.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
                    proc.StandardInput.Close();

                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                    // Bounded by `ct` as well: a grandchild that inherited the
                    // pipe could hold it open after the child itself exited.
                    var output = await stdout.WaitAsync(ct).ConfigureAwait(false);
                    await stderr.WaitAsync(ct).ConfigureAwait(false);

                    if (proc.ExitCode == 0) return output;
                    report?.Invoke(BubbleFailure.NonZeroExit);
                    return null;
                }
                catch
                {
                    // Cancelled (the controller's timeout, or the moment went
                    // stale), or stdin broke because the child died early.
                    // Either way nothing it prints next is wanted, and a
                    // `claude` left running would keep spending the user's
                    // usage on a bubble nobody will see — so the whole tree
                    // goes, which on Windows includes the process under a .cmd
                    // shim.
                    Kill(proc);
                    if (!ct.IsCancellationRequested) report?.Invoke(BubbleFailure.ProcessFailed);
                    return null;
                }
                finally
                {
                    InternalSessions.Forget(pid);
                }
            }
            catch
            {
                // The binary vanished between locate and start, or the OS
                // refused the spawn.
                report?.Invoke(BubbleFailure.StartFailed);
                return null;
            }
            finally
            {
                proc?.Dispose();
            }
        }

        // Tolerates a process that has already exited (.NET says so with
        // InvalidOperationException), one this user may not touch
        // (Win32Exception) and a tree only partly killed (AggregateException).
        // There is nothing further to do about any of them, and none may
        // escape: this runs inside a catch that promises null.
        internal static void Kill(Process proc)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { }
        }
    }
}
