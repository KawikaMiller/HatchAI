using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace HatchAI
{
    // HatchAI's own settings file, and the buddy's persisted state inside it.
    //
    // Replaces Claude Buddy's ClaudeBuddySettings for the buddy's purposes,
    // keeping the member names the ported buddy files call (BuddyEnabled,
    // BuddyObject, UpdateBuddy, OrbPositionFor and so on) so they port with a
    // one-token edit. It is deliberately **never** Claude Buddy's
    // settings.json: both apps hold their own in-memory copy and rewrite the
    // whole file on save, so sharing one would make the last writer win on the
    // whole "buddy" object — cursors moving backwards, tokens recounted or
    // lost, and nothing saying so.
    //
    // The whole file is held as the JsonObject it was read as, and a setter
    // mutates only the key it owns. That is what Claude Buddy needed an
    // _unknownKeys list and a separately held "buddy" node for: here a key
    // this build does not know — at the top level or at any depth — is simply
    // never touched, so it is written back exactly as it was found, and a
    // downgrade cannot erase what a newer build added.
    //
    // The shape:
    //
    //     { "version": 1,
    //       "buddyEnabled": true, "buddyBubblesEnabled": true,
    //       "buddyIdleBubblesEnabled": false, "buddyAiBubblesEnabled": false,
    //       "buddyBubbleLogEnabled": true,
    //       "orbPositions": { "buddy": { "x": 0, "y": 0 } },
    //       "claudeCodeProfileDirs": [], "codexHomes": [],
    //       "buddy": { ...BuddyStore's shape... } }
    //
    // "orbPositions" keeps Claude Buddy's name and map shape rather than
    // becoming a single "buddyPosition", so BuddyWindow ports unchanged and
    // an imported position is a copy, not a translation.
    //
    // Every public member loads on first use and saves synchronously, on the
    // caller's thread — the UI thread in the app, as in Claude Buddy. The one
    // high-frequency writer (the ledger's cursors) is already throttled by
    // BuddyController.SaveEvery.
    internal static class HatchAISettings
    {
        private static readonly object Gate = new();
        private static JsonObject _root = new();
        private static bool _loaded;

        // Set when a file was there but could not be read *and* could not be
        // set aside. Saving then would replace a file nobody has seen with
        // defaults, so every save is refused instead for the life of the
        // process: preferences changed this session are lost, the file on
        // disk is not.
        private static bool _saveBlocked;

        // Written with the same TypeInfoResolver Claude Buddy had to add: the
        // shipped build is SelfContained + PublishSingleFile, which trims, and
        // JsonNode.ToJsonString(options) without an explicit resolver threw on
        // every save there while every `dotnet run` looked fine (Claude
        // Buddy's comment on its own SaveOptions records the repro). The
        // values are plain primitives, so reflection over them is safe.
        private static readonly JsonSerializerOptions SaveOptions = new()
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        internal const int FileVersion = 1;

        // Where a dragged window sits, in raw screen pixels — the same record
        // Claude Buddy uses, so BuddyWindow ports without an edit.
        internal sealed record OrbPlacement(int X, int Y);

        // %APPDATA%\HatchAI on Windows; ~/Library/Application Support/HatchAI
        // spelled out on macOS rather than trusted to SpecialFolder, whose
        // mapping for ApplicationData on macOS is not something this project
        // has verified (the extraction plan's §3 records the doubt).
        //
        // Test seam: HATCHAI_SETTINGS_DIR. Without it a test that so much as
        // reads a preference reads the developer's real file, and one that
        // writes a preference writes it for good.
        public static string Directory =>
            Environment.GetEnvironmentVariable("HATCHAI_SETTINGS_DIR") is { Length: > 0 } scratch
                ? scratch
                : DefaultDirectory;

        // Excluded from coverage: reads the real user profile. Every test runs
        // with HATCHAI_SETTINGS_DIR pointed at a scratch directory, which is
        // the point of the seam above.
        [ExcludeFromCodeCoverage]
        private static string DefaultDirectory =>
            OperatingSystem.IsMacOS()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "HatchAI")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HatchAI");

        public static string Path_ => Path.Combine(Directory, "settings.json");

        // ---- preferences ----------------------------------------------------

        // Defaults are Claude Buddy's, so an imported or a fresh buddy behaves
        // the same in both apps.
        public static bool BuddyEnabled
        {
            get => GetBool("buddyEnabled", true);
            set => SetValue("buddyEnabled", value);
        }

        public static bool BuddyBubblesEnabled
        {
            get => GetBool("buddyBubblesEnabled", true);
            set => SetValue("buddyBubblesEnabled", value);
        }

        // The opt-in for generated lines, which spend the user's own Claude
        // usage. Off by default and never switched on by anything but the user.
        public static bool BuddyAiBubblesEnabled
        {
            get => GetBool("buddyAiBubblesEnabled", false);
            set => SetValue("buddyAiBubblesEnabled", value);
        }

        // Whether the long-idle moment may speak at all. Off by default.
        public static bool BuddyIdleBubblesEnabled
        {
            get => GetBool("buddyIdleBubblesEnabled", false);
            set => SetValue("buddyIdleBubblesEnabled", value);
        }

        // Whether bubble decisions are written to bubble-log.jsonl beside this
        // file. On by default.
        public static bool BuddyBubbleLogEnabled
        {
            get => GetBool("buddyBubbleLogEnabled", true);
            set => SetValue("buddyBubbleLogEnabled", value);
        }

        // ---- buddy ----------------------------------------------------------

        // A detached copy of the "buddy" object, or null when there is none
        // yet (or it is not an object). A copy so a reader off the UI thread
        // never sees an edit half-applied.
        internal static JsonObject? BuddyObject()
        {
            Load();
            lock (Gate) return _root["buddy"] is JsonObject buddy ? (JsonObject)buddy.DeepClone() : null;
        }

        // Edits the "buddy" object in place and writes the file once. `edit`
        // is handed the live object — created empty if there was none, or if
        // what was there was not an object — so whatever it does not touch
        // survives exactly as it was read. A foreign non-object value is kept
        // until this first runs, which is the first hatch: the same trade
        // Claude Buddy's UpdateBuddy makes, since there is one key and this
        // build's buddy has to live in it.
        internal static void UpdateBuddy(Action<JsonObject> edit)
        {
            Load();
            lock (Gate)
            {
                if (_root["buddy"] is not JsonObject buddy)
                {
                    buddy = new JsonObject();
                    _root["buddy"] = buddy;
                }

                edit(buddy);
            }

            Save();
        }

        // ---- window position ------------------------------------------------

        public static OrbPlacement? OrbPositionFor(string key)
        {
            Load();
            lock (Gate)
            {
                if (_root["orbPositions"] is not JsonObject positions
                    || positions[key] is not JsonObject at
                    || Number(at["x"]) is not { } x
                    || Number(at["y"]) is not { } y)
                {
                    return null;
                }

                return new OrbPlacement(x, y);
            }
        }

        public static void SetOrbPosition(string key, int x, int y)
        {
            Load();
            lock (Gate)
            {
                if (_root["orbPositions"] is not JsonObject positions)
                {
                    positions = new JsonObject();
                    _root["orbPositions"] = positions;
                }

                if (positions[key] is JsonObject at && Number(at["x"]) == x && Number(at["y"]) == y) return;

                // Edited in place when it is already an object, so a key a
                // newer build put beside x and y survives.
                if (positions[key] is JsonObject existing)
                {
                    existing["x"] = x;
                    existing["y"] = y;
                }
                else
                {
                    positions[key] = new JsonObject { ["x"] = x, ["y"] = y };
                }
            }

            Save();
        }

        public static void ClearOrbPosition(string key)
        {
            Load();
            lock (Gate)
            {
                if (_root["orbPositions"] is not JsonObject positions || !positions.Remove(key)) return;
            }

            Save();
        }

        // ---- extra config roots for the ledger -------------------------------

        // Extra Claude Code config folders (besides ~/.claude) and Codex homes
        // (besides ~/.codex) whose transcripts count toward the buddy. Copies,
        // so a caller cannot mutate the store without going through Add/Remove.
        public static IReadOnlyList<string> ClaudeCodeProfileDirs => GetList("claudeCodeProfileDirs");

        public static void AddClaudeCodeProfileDir(string dirName) => AddToList("claudeCodeProfileDirs", dirName);

        public static void RemoveClaudeCodeProfileDir(string dirName) => RemoveFromList("claudeCodeProfileDirs", dirName);

        public static IReadOnlyList<string> CodexHomes => GetList("codexHomes");

        public static void AddCodexHome(string dirName) => AddToList("codexHomes", dirName);

        public static void RemoveCodexHome(string dirName) => RemoveFromList("codexHomes", dirName);

        // ---- import from Claude Buddy ----------------------------------------

        internal enum ImportOutcome { Imported, NotFound, Unreadable, NoBuddy }

        // Where Claude Buddy keeps its settings — its own exact expression,
        // ApplicationData + "ClaudeBuddy", whatever that resolves to, since
        // this has to find the file Claude Buddy actually writes rather than
        // the one this app would have chosen.
        //
        // Excluded from coverage: reads the real user profile; every test
        // passes its own path to ImportFromClaudeBuddy.
        [ExcludeFromCodeCoverage]
        internal static string ClaudeBuddySettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClaudeBuddy", "settings.json");

        // Copies Claude Buddy's buddy into this file, once, on the user's
        // say-so: its "buddy" object, its "orbPositions.buddy", and its extra
        // Claude Code and Codex folders (merged, not replaced). Claude Buddy's
        // file is opened read-only and shared for writing and deleting, so
        // Claude Buddy saving at the same moment is neither blocked nor
        // disturbed; nothing is ever written to it.
        //
        // Replaces any buddy this file already holds. The Settings window asks
        // first, and BuddyController.Reload must be called afterwards, or the
        // running controller's next save writes its old buddy straight back
        // over the imported one.
        //
        // NoBuddy leaves this file untouched: a Claude Buddy that never hatched
        // one has nothing worth copying, and copying only its folders would be
        // a surprising half-import.
        internal static ImportOutcome ImportFromClaudeBuddy(string sourcePath)
        {
            JsonObject source;
            try
            {
                if (!File.Exists(sourcePath)) return ImportOutcome.NotFound;

                using var stream = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (JsonNode.Parse(stream) is not JsonObject parsed) return ImportOutcome.Unreadable;
                source = parsed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return ImportOutcome.Unreadable;
            }

            if (source["buddy"] is not JsonObject buddy) return ImportOutcome.NoBuddy;

            Load();
            lock (Gate)
            {
                _root["buddy"] = buddy.DeepClone();

                if (source["orbPositions"] is JsonObject theirPositions
                    && theirPositions["buddy"] is JsonObject theirs
                    && Number(theirs["x"]) is { } x && Number(theirs["y"]) is { } y)
                {
                    if (_root["orbPositions"] is not JsonObject positions)
                    {
                        positions = new JsonObject();
                        _root["orbPositions"] = positions;
                    }
                    positions["buddy"] = new JsonObject { ["x"] = x, ["y"] = y };
                }

                MergeList("claudeCodeProfileDirs", source["claudeCodeProfileDirs"]);
                MergeList("codexHomes", source["codexHomes"]);
            }

            Save();
            return ImportOutcome.Imported;
        }

        // ---- storage --------------------------------------------------------

        // Test seam: the class is static and caches the file for the life of
        // the process, so a test that points HATCHAI_SETTINGS_DIR somewhere
        // new needs this to make that folder actually get read.
        internal static void ReloadForTests()
        {
            lock (Gate)
            {
                _root = new JsonObject();
                _loaded = false;
                _saveBlocked = false;
            }

            Load();
        }

        // Whether saves are being refused because the file on disk could not
        // be read or set aside. For tests and the settings window.
        internal static bool SaveBlocked
        {
            get { Load(); lock (Gate) return _saveBlocked; }
        }

        private static void Load()
        {
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;

                if (!File.Exists(Path_)) return;

                try
                {
                    // A leading BOM (a hand edit in an old Notepad) is not
                    // valid JSON to JsonNode; strip it rather than lose the
                    // file over it.
                    var text = File.ReadAllText(Path_, Encoding.UTF8).TrimStart('﻿');
                    if (JsonNode.Parse(text) is JsonObject root)
                    {
                        _root = root;
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    CrashLog.Record("HatchAISettings.Load", ex);
                }

                // There is a file and it is not a settings object: unparseable,
                // or valid JSON of the wrong shape. Claude Buddy resets to
                // defaults here and its next save erases the file. This app's
                // file holds a pet somebody may have spent months growing, so
                // the file is set aside first, under a name that says when, and
                // only then does the app carry on from defaults. If it cannot
                // even be copied, nothing is ever written over it.
                SetAsideUnreadable();
            }
        }

        private static void SetAsideUnreadable()
        {
            try
            {
                var aside = Path_ + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(Path_, aside, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLog.Record("HatchAISettings.SetAside", ex);
                _saveBlocked = true;
            }
        }

        private static void Save()
        {
            string json;
            lock (Gate)
            {
                if (_saveBlocked) return;
                if (_root["version"] is null) _root["version"] = FileVersion;
                json = _root.ToJsonString(SaveOptions);
            }

            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                // Written beside the target and renamed over it, so a crash
                // midway cannot leave an unparseable file. UTF-8 without a BOM,
                // which is what JsonNode reads back without complaint.
                var temporary = Path_ + ".tmp";
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Move(temporary, Path_, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing a preference is not worth taking the app down for,
                // but losing it with no trace is the "no error, it just doesn't
                // work" trap. The breadcrumb goes to the crash log's folder —
                // never the status folder Claude Buddy's settings logs to,
                // which belongs to the hooks and which HatchAI only reads.
                CrashLog.Record("HatchAISettings.Save", ex);
            }
        }

        private static bool GetBool(string key, bool fallback)
        {
            Load();
            lock (Gate) return Bool(_root[key], fallback);
        }

        private static void SetValue(string key, bool value)
        {
            Load();
            lock (Gate) _root[key] = value;
            Save();
        }

        private static IReadOnlyList<string> GetList(string key)
        {
            Load();
            lock (Gate) return Strings(_root[key]);
        }

        private static void AddToList(string key, string value)
        {
            Load();
            lock (Gate)
            {
                var array = ListFor(key);
                if (Strings(array).Contains(value, StringComparer.Ordinal)) return;
                array.Add(value);
            }

            Save();
        }

        private static void RemoveFromList(string key, string value)
        {
            Load();
            lock (Gate)
            {
                if (_root[key] is not JsonArray array) return;
                var hit = array.FirstOrDefault(n => Text(n) == value);
                if (hit is null) return;
                array.Remove(hit);
            }

            Save();
        }

        // Adds each string in `from` that the list does not already hold.
        // Caller holds Gate.
        private static void MergeList(string key, JsonNode? from)
        {
            var incoming = Strings(from);
            if (incoming.Count == 0) return;

            var array = ListFor(key);
            var have = new HashSet<string>(Strings(array), StringComparer.Ordinal);
            foreach (var value in incoming)
                if (have.Add(value)) array.Add(value);
        }

        // The list under `key`, created when absent — or when what is there is
        // not a list, since the caller is about to write one. Caller holds Gate.
        private static JsonArray ListFor(string key)
        {
            if (_root[key] is JsonArray array) return array;
            array = new JsonArray();
            _root[key] = array;
            return array;
        }

        // ---- defensive readers ----------------------------------------------

        // Each reader answers for one value and never throws: a hand-edited or
        // mistyped value costs that value, never the file. Claude Buddy learned
        // this one key at a time (its Text/Number/Bool); here it is the only
        // way anything is read.
        internal static bool Bool(JsonNode? node, bool fallback) =>
            node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;

        internal static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

        internal static int? Number(JsonNode? node)
        {
            if (node is not JsonValue value) return null;
            if (value.TryGetValue<int>(out var i)) return i;
            // A position written as 12.0 by some other tool is still 12.
            if (value.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
                return (int)d;
            return null;
        }

        // Every non-empty string in an array, skipping anything else per entry
        // so one bad element costs only itself.
        internal static List<string> Strings(JsonNode? node)
        {
            var list = new List<string>();
            if (node is not JsonArray array) return list;
            foreach (var item in array)
                if (Text(item) is { Length: > 0 } s) list.Add(s);
            return list;
        }
    }
}
