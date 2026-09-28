using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace HatchAI
{
    // Installing HatchAI's own session hooks from inside the app, and telling
    // whether they are installed.
    //
    // The hooks are how HatchAI learns what sessions are doing (see
    // Hooks/HatchAIHook.ps1). Until they are wired into Claude Code, Codex or
    // Grok nothing fails — the buddy simply never reacts — so the Settings
    // window offers the install as a button, with the current state beside it.
    //
    // The installer scripts are embedded in the executable rather than shipped
    // beside it, because HatchAI publishes as a single file and a loose script
    // next to it is one more thing to lose. They are written to a fresh temp
    // folder for each run, run from there with Windows PowerShell 5.1 (the
    // engine every wired command uses), and the folder is deleted afterwards.
    // The script that is kept is the hook itself, which the installer copies
    // to %LOCALAPPDATA%\HatchAI before wiring commands to it.
    //
    // Safe to run any number of times: every installer strips HatchAI's own
    // entries and adds them fresh, never touches anybody else's (Claude
    // Buddy's included), and writes nothing when the result would be the same.
    // Hooks/hatchai-hooks-common.ps1 has the rules and the tests that pin them.
    internal static class HookSetup
    {
        internal const string ResourcePrefix = "HatchAI.Hooks.";

        // Long enough for a cold PowerShell start plus three small installers.
        // A backstop against a hung child, not a budget.
        private const int TimeoutMs = 60_000;

        internal sealed record Result(bool Succeeded, IReadOnlyList<string> Summary, string Output);

        // Explicit targets, for a test. Production passes none, and the
        // installers then use the real defaults — which is the point of the
        // button. Environment is layered onto the child only.
        internal sealed record Options(
            string? SettingsPath = null,
            string? InstallDir = null,
            string? CodexHome = null,
            string? GrokHome = null,
            string? TempDir = null,
            IReadOnlyDictionary<string, string>? Environment = null);

        // ---- the embedded scripts ------------------------------------------------

        internal static IReadOnlyList<string> EmbeddedScripts() =>
            typeof(HookSetup).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .Select(n => n[ResourcePrefix.Length..])
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        // Writes every embedded script into `directory`, byte for byte.
        internal static void ExtractTo(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (var name in EmbeddedScripts())
            {
                var path = Path.Combine(directory, name);
                using (var source = typeof(HookSetup).Assembly.GetManifestResourceStream(ResourcePrefix + name)!)
                using (var target = File.Create(path))
                {
                    source.CopyTo(target);
                }

                // The macOS installers re-invoke themselves for each extra
                // profile, which needs them executable.
                if (!OperatingSystem.IsWindows() && name.EndsWith(".sh", StringComparison.Ordinal))
                    File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
            }
        }

        // ---- running it ----------------------------------------------------------

        // The whole command, as a file to run and its arguments. Pure.
        internal static (string File, List<string> Arguments) Command(
            string scriptDirectory, IReadOnlyList<string> profileDirs, Options options, bool windows)
        {
            var args = new List<string>();
            string file;

            if (windows)
            {
                file = Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                args.AddRange(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                    Path.Combine(scriptDirectory, "install-hooks.ps1") });

                void Add(string name, string? value)
                {
                    if (string.IsNullOrEmpty(value)) return;
                    args.Add("-" + name);
                    args.Add(value);
                }

                Add("SettingsPath", options.SettingsPath);
                Add("InstallDir", options.InstallDir);
                Add("CodexHome", options.CodexHome);
                Add("GrokHome", options.GrokHome);
                Add("TempDir", options.TempDir);
                // One -File argument can carry only one string.
                if (profileDirs.Count > 0) Add("ProfileDirs", string.Join(";", profileDirs));
            }
            else
            {
                // The macOS entry point takes --uninstall and nothing else; its
                // sub-installers read HatchAI's saved profiles themselves.
                // Ported, not verified.
                file = "/bin/bash";
                args.Add(Path.Combine(scriptDirectory, "install-hooks.sh"));
            }

            return (file, args);
        }

        // What the installer said, as the Settings row shows it. Pure.
        internal static Result Interpret(int exitCode, string stdout, string stderr)
        {
            var summary = stdout.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("SUMMARY ", StringComparison.Ordinal))
                .Select(l => l["SUMMARY ".Length..])
                .ToList();

            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
            return new Result(exitCode == 0 && summary.Count > 0, summary, output);
        }

        // Extract, run, interpret, clean up. Never throws: a failure is a
        // Result the Settings row can show.
        internal static Task<Result> RunAsync(Options? options = null) => Task.Run(() => Run(options ?? new Options()));

        // With empty Options this runs against the real configuration, which
        // is what the button is for and what no test may do; the integration
        // suite drives it with scratch Options and the installer's sandbox on.
        internal static Result Run(Options options)
        {
            var directory = Path.Combine(Path.GetTempPath(), "hatchai-hook-setup-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                ExtractTo(directory);
                var (file, args) = Command(directory, HatchAISettings.ClaudeCodeProfileDirs, options, OperatingSystem.IsWindows());

                var psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                if (options.Environment is { } env)
                    foreach (var (key, value) in env) psi.Environment[key] = value;

                using var process = Process.Start(psi);
                if (process is null) return new Result(false, Array.Empty<string>(), "The installer could not be started.");

                // Both streams drained before waiting, or a chatty child fills a
                // pipe and the timeout fires on a run that was fine.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return new Result(false, Array.Empty<string>(), "The installer did not finish within a minute.");
                }

                return Interpret(process.ExitCode, stdout.Result, stderr.Result);
            }
            catch (Exception ex)
            {
                return new Result(false, Array.Empty<string>(), ex.Message);
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        // ---- whether they are installed -----------------------------------------

        internal enum Presence { None, ClaudeBuddyOnly, HatchAI }

        // Which app's hooks a config file carries. Pure: the text of the file,
        // or null when there is none. Anything unparseable reads as None,
        // since that is what the CLI itself would make of it.
        internal static Presence PresenceIn(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Presence.None;

            JsonNode? root;
            try { root = JsonNode.Parse(json); }
            catch (System.Text.Json.JsonException) { return Presence.None; }

            if (root is not JsonObject obj || obj["hooks"] is not JsonObject hooks) return Presence.None;

            var claudeBuddy = false;
            foreach (var (_, groups) in hooks)
            {
                if (groups is not JsonArray list) continue;
                foreach (var group in list)
                {
                    if (group is not JsonObject g || g["hooks"] is not JsonArray handlers) continue;
                    foreach (var handler in handlers)
                    {
                        if (handler is not JsonObject h) continue;
                        foreach (var field in new[] { "command", "commandWindows" })
                        {
                            if (h[field] is not JsonValue v || !v.TryGetValue<string>(out var command)) continue;
                            if (command.Contains("HatchAIHook.", StringComparison.Ordinal)) return Presence.HatchAI;
                            if (command.Contains("ClaudeBuddyHook.", StringComparison.Ordinal)) claudeBuddy = true;
                        }
                    }
                }
            }

            return claudeBuddy ? Presence.ClaudeBuddyOnly : Presence.None;
        }

        // The one line the Settings row shows about the current state. Pure.
        internal static string Describe(Presence claudeCode, Presence codex, Presence grok)
        {
            var ours = new List<string>();
            if (claudeCode == Presence.HatchAI) ours.Add("Claude Code");
            if (codex == Presence.HatchAI) ours.Add("Codex");
            if (grok == Presence.HatchAI) ours.Add("Grok");

            var borrowed = claudeCode == Presence.ClaudeBuddyOnly || codex == Presence.ClaudeBuddyOnly
                || grok == Presence.ClaudeBuddyOnly;

            if (ours.Count > 0) return "Installed for " + string.Join(", ", ours) + ".";
            if (borrowed)
                return "Not installed yet. Claude Buddy's hooks are installed on this computer, so HatchAI "
                    + "can see your sessions through them for now.";
            return "Not installed yet. Until they are, the buddy can't see your sessions.";
        }

        // Reads the three CLIs' real config files, read-only, to describe
        // them. Excluded from coverage: it reads the real user profile, which
        // no test may; Describe and PresenceIn are what it decides with.
        [ExcludeFromCodeCoverage]
        internal static string DescribeThisMachine()
        {
            static string? Read(string path)
            {
                try { return File.Exists(path) ? File.ReadAllText(path) : null; }
                catch { return null; }
            }

            var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            var codexHome = System.Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".codex");
            var grokHome = System.Environment.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } g ? g : Path.Combine(home, ".grok");

            // Grok: HatchAI's own file, or Claude Buddy's beside it.
            var grok = PresenceIn(Read(Path.Combine(grokHome, "hooks", "hatchai.json")));
            if (grok == Presence.None) grok = PresenceIn(Read(Path.Combine(grokHome, "hooks", "claude-buddy.json")));

            return Describe(
                PresenceIn(Read(Path.Combine(home, ".claude", "settings.json"))),
                PresenceIn(Read(Path.Combine(codexHome, "hooks.json"))),
                grok);
        }

        // What to tell the person after a run. Pure.
        internal static string OutcomeText(Result result)
        {
            if (result.Succeeded)
                return "Done. " + string.Join(" ", result.Summary.Select(s => s.TrimEnd('.') + "."))
                    + " Restart any running sessions so they pick the hooks up."
                    // Codex runs no hook it has not been told to trust, and
                    // says nothing about it — the installer's own warning,
                    // which the button would otherwise swallow.
                    + (result.Summary.Contains("Codex: wired")
                        ? " Codex will ask you to trust the new hooks the next time it starts; until you do, it won't run them."
                        : "");

            var lines = result.Summary.Where(s => s.Contains("failed", StringComparison.Ordinal)).ToList();
            var detail = lines.Count > 0 ? string.Join(" ", lines.Select(s => s.TrimEnd('.') + "."))
                : result.Output.Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            return "The hooks could not be installed. " + detail
                + (detail.Length > 0 ? " " : "") + "Nothing that was already in your settings was changed.";
        }
    }
}
