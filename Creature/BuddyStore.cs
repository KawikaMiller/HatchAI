using System.Globalization;
using System.Text.Json.Nodes;

namespace HatchAI
{
    // IBuddyStore over the top-level "buddy" object in settings.json. Owned by
    // E2 (CB-195).
    //
    // The shape on disk:
    //
    //     "buddy": {
    //       "uuid": "…", "rebirths": 0, "countingSince": "2026-09-25T…Z",
    //       "tokens": 0, "lifetimeTokens": 0, "name": null, "stars": 0,
    //       "history": [ { "rebirth", "name", "species", "rarity", "shiny",
    //                      "stage", "stars", "tokens", "retiredAt" } ],
    //       "ledger": {
    //         "files": { "<absolute path>": { "offset", "lastTimestamp",
    //                    "openMessageId", "openMessageOutput", "lastCumulative" } },
    //         "recentIds": [ "msg_…" ]
    //       }
    //     }
    //
    // Save never rebuilds that object. It sets the fields it knows on the
    // object it was handed and leaves every other key alone, at every depth
    // it writes to — the whole reason the object is a JsonObject and not a
    // serialised record (see HatchAISettings._root).
    //
    // Enums are written by name, not number. BuddyTaxonomy's declaration
    // order is pinned for hatching, but a history entry is a snapshot of what
    // a buddy *was*, and a name reads the same in any build that knows it.
    internal sealed class BuddyStore : IBuddyStore
    {
        private readonly Func<DateTimeOffset> _now;

        // `now` is only for repairing a stored buddy that has lost its
        // countingSince — see Parse.
        internal BuddyStore(Func<DateTimeOffset>? now = null)
        {
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        // Null only when there is no "buddy" object at all. An object whose
        // uuid is missing or blank is repaired in place instead (QA on
        // CB-195): answering null for it made the controller hatch a fresh
        // buddy *over* it — lifetime total and rebirth count written back as
        // zero — and since Write appends history only past the length already
        // on disk, the next rebirth's retired buddy was never written at all.
        // A new uuid means a new look, which cannot be helped once the old one
        // is gone; everything the user earned is kept, and the repair is
        // saved at once, like a hatch, so a crash cannot roll a third.
        public BuddyState? Load()
        {
            if (HatchAISettings.BuddyObject() is not { } buddy) return null;
            if (Parse(buddy, _now()) is { } state) return state;

            buddy["uuid"] = Guid.NewGuid().ToString();
            var repaired = Parse(buddy, _now())!;
            Save(repaired);
            return repaired;
        }

        // One settings write, so the counts and the cursors that earned them
        // land together or not at all.
        public void Save(BuddyState state) => HatchAISettings.UpdateBuddy(buddy => Write(buddy, state));

        // ---- reading --------------------------------------------------------

        // Every field is read defensively, the same discipline as
        // HatchAISettings.Text/Number/Bool: a hand-edited or mistyped value
        // costs that value, never the buddy. The uuid is the one exception —
        // without it there is no buddy to speak of, and null says so; Load is
        // what decides to give it a new one rather than start over.
        internal static BuddyState? Parse(JsonObject? buddy, DateTimeOffset now)
        {
            if (buddy is null || Text(buddy["uuid"]) is not { } uuid) return null;

            var history = new List<BuddyHistoryEntry>();
            if (buddy["history"] is JsonArray entries)
            {
                // One entry per element, whatever it holds, so the list lines
                // up with the array by index — Save appends by that index.
                foreach (var node in entries) history.Add(HistoryEntry(node as JsonObject));
            }

            var cursors = new Dictionary<string, LedgerCursor>(StringComparer.Ordinal);
            var recent = new List<string>();
            if (buddy["ledger"] is JsonObject ledger)
            {
                if (ledger["files"] is JsonObject files)
                {
                    foreach (var (path, node) in files)
                    {
                        if (Cursor(node) is { } cursor) cursors[path] = cursor;
                    }
                }

                if (ledger["recentIds"] is JsonArray ids)
                {
                    foreach (var node in ids)
                    {
                        if (Text(node) is { } id) recent.Add(id);
                    }
                }
            }

            return new BuddyState(
                uuid,
                (int)Math.Min(Long(buddy["rebirths"]), int.MaxValue),
                // Missing countingSince would otherwise mean "count everything
                // ever", which hatches a third-stage buddy off old history.
                // Now is the same answer a first hatch gets.
                Time(buddy["countingSince"]) ?? now,
                Long(buddy["tokens"]),
                Long(buddy["lifetimeTokens"]),
                Text(buddy["name"]),
                (int)Math.Min(Long(buddy["stars"]), int.MaxValue),
                history,
                cursors,
                recent);
        }

        private static BuddyHistoryEntry HistoryEntry(JsonObject? entry) => new(
            (int)Math.Min(Long(entry?["rebirth"]), int.MaxValue),
            Text(entry?["name"]) ?? "",
            Enum<BuddySpecies>(entry?["species"]),
            Enum<BuddyRarity>(entry?["rarity"]),
            entry?["shiny"] is JsonValue shiny && shiny.TryGetValue<bool>(out var isShiny) && isShiny,
            Enum<BuddyStage>(entry?["stage"]),
            (int)Math.Min(Long(entry?["stars"]), int.MaxValue),
            Long(entry?["tokens"]),
            Time(entry?["retiredAt"]) ?? DateTimeOffset.MinValue);

        // A cursor needs an offset to mean anything; an entry without one is
        // not this build's, and is left for whoever wrote it.
        private static LedgerCursor? Cursor(JsonNode? node)
        {
            if (node is not JsonObject cursor) return null;
            if (cursor["offset"] is not JsonValue offset || !offset.TryGetValue<long>(out var at) || at < 0)
                return null;

            return new LedgerCursor(
                at,
                Time(cursor["lastTimestamp"]),
                Text(cursor["openMessageId"]),
                Long(cursor["openMessageOutput"]),
                Long(cursor["lastCumulative"]));
        }

        // ---- writing --------------------------------------------------------

        internal static void Write(JsonObject buddy, BuddyState state)
        {
            buddy["uuid"] = state.Uuid;
            buddy["rebirths"] = state.Rebirths;
            buddy["countingSince"] = Stamp(state.CountingSince);
            buddy["tokens"] = state.Tokens;
            buddy["lifetimeTokens"] = state.LifetimeTokens;
            buddy["name"] = state.Name;
            buddy["stars"] = state.Stars;

            // History is append-only: an entry already on disk is a snapshot
            // of a retired buddy and is never rewritten, which also means a
            // key a newer build added to one is never at risk. Only entries
            // past the end of the array are new.
            if (buddy["history"] is not JsonArray history)
            {
                history = new JsonArray();
                buddy["history"] = history;
            }

            for (var i = history.Count; i < state.History.Count; i++)
            {
                var entry = state.History[i];
                history.Add(new JsonObject
                {
                    ["rebirth"] = entry.Rebirth,
                    ["name"] = entry.Name,
                    ["species"] = entry.Species.ToString(),
                    ["rarity"] = entry.Rarity.ToString(),
                    ["shiny"] = entry.Shiny,
                    ["stage"] = entry.Stage.ToString(),
                    ["stars"] = entry.Stars,
                    ["tokens"] = entry.Tokens,
                    ["retiredAt"] = Stamp(entry.RetiredAt)
                });
            }

            if (buddy["ledger"] is not JsonObject ledger)
            {
                ledger = new JsonObject();
                buddy["ledger"] = ledger;
            }

            if (ledger["files"] is not JsonObject files)
            {
                files = new JsonObject();
                ledger["files"] = files;
            }

            // A cursor this build wrote and the state no longer has — its file
            // was pruned — goes. An entry this build cannot read as a cursor
            // is someone else's and stays.
            foreach (var path in files.Select(pair => pair.Key).ToList())
            {
                if (!state.Cursors.ContainsKey(path) && Cursor(files[path]) is not null) files.Remove(path);
            }

            foreach (var (path, cursor) in state.Cursors)
            {
                if (files[path] is not JsonObject file)
                {
                    file = new JsonObject();
                    files[path] = file;
                }

                file["offset"] = cursor.Offset;
                file["lastTimestamp"] = cursor.LastTimestamp is { } last ? Stamp(last) : null;
                file["openMessageId"] = cursor.OpenMessageId;
                file["openMessageOutput"] = cursor.OpenMessageOutput;
                file["lastCumulative"] = cursor.LastCumulative;
            }

            var ids = new JsonArray();
            foreach (var id in state.RecentMessageIds) ids.Add(id);
            ledger["recentIds"] = ids;
        }

        // ---- values ---------------------------------------------------------

        private static string Stamp(DateTimeOffset at) =>
            at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;

        // Non-negative, or 0. Accepts a whole-valued double too, since a
        // hand-edit or another JSON writer can turn 12000 into 12000.0.
        private static long Long(JsonNode? node)
        {
            if (node is not JsonValue value) return 0;
            if (value.TryGetValue<long>(out var whole)) return Math.Max(0, whole);
            if (value.TryGetValue<double>(out var real) && real >= 0 && real <= long.MaxValue)
                return (long)real;
            return 0;
        }

        private static DateTimeOffset? Time(JsonNode? node) =>
            Text(node) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;

        // By name, case-insensitively; a name this build does not know (a
        // species a newer build appended) reads as the first member rather
        // than failing the load. Display only — Save never rewrites an
        // existing history entry, so the real name stays on disk.
        private static T Enum<T>(JsonNode? node) where T : struct, System.Enum =>
            Text(node) is { } name
            && System.Enum.TryParse<T>(name, ignoreCase: true, out var parsed)
            && System.Enum.IsDefined(parsed)
                ? parsed
                : default;
    }
}
