using System.Text.Json.Nodes;
using Xunit;

namespace HatchAI.Tests
{
    // BuddyStore's JSON mapping, pure (CB-195): Parse and Write against a
    // JsonObject, no settings.json. The round trip through a real file is
    // BuddyStoreFileTests in tests/IntegrationTests.
    public class BuddyStoreTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");

        private static BuddyState Sample() => BuddyState.Hatch("uuid-1", Now) with
        {
            Rebirths = 2,
            Tokens = 12_345,
            LifetimeTokens = 99_999,
            Name = "Pip",
            Stars = 1,
            History = new[]
            {
                new BuddyHistoryEntry(0, "Quack", BuddySpecies.Goose, BuddyRarity.Rare, true,
                    BuddyStage.Hatchling, 3, 70_000, Now.AddDays(-2)),
                new BuddyHistoryEntry(1, "Bloop", BuddySpecies.Blob, BuddyRarity.Common, false,
                    BuddyStage.Hatchling, 0, 30_000, Now.AddDays(-1)),
            },
            Cursors = new Dictionary<string, LedgerCursor>
            {
                ["/a.jsonl"] = new(120, Now, "msg_1", 40, 0),
                ["/rollout-b.jsonl"] = new(80, null, null, 0, 900),
            },
            RecentMessageIds = new[] { "msg_0", "msg_1" }
        };

        [Fact]
        public void WriteThenParseGivesBackTheSameState()
        {
            var state = Sample();
            var json = new JsonObject();
            BuddyStore.Write(json, state);

            var back = BuddyStore.Parse(JsonNode.Parse(json.ToJsonString())!.AsObject(), Now.AddYears(1))!;

            Assert.Equal(state.Uuid, back.Uuid);
            Assert.Equal(state.Rebirths, back.Rebirths);
            Assert.Equal(state.CountingSince, back.CountingSince);
            Assert.Equal(state.Tokens, back.Tokens);
            Assert.Equal(state.LifetimeTokens, back.LifetimeTokens);
            Assert.Equal(state.Name, back.Name);
            Assert.Equal(state.Stars, back.Stars);
            Assert.Equal(state.History, back.History);
            Assert.Equal(state.Cursors, back.Cursors);
            Assert.Equal(state.RecentMessageIds, back.RecentMessageIds);
        }

        [Fact]
        public void EnumsAreWrittenByName()
        {
            var json = new JsonObject();
            BuddyStore.Write(json, Sample());

            var entry = json["history"]![0]!;
            Assert.Equal("Goose", entry["species"]!.GetValue<string>());
            Assert.Equal("Rare", entry["rarity"]!.GetValue<string>());
            Assert.Equal("Hatchling", entry["stage"]!.GetValue<string>());
        }

        [Fact]
        public void NoObjectOrNoUuidIsNoBuddy()
        {
            Assert.Null(BuddyStore.Parse(null, Now));
            Assert.Null(BuddyStore.Parse(new JsonObject { ["tokens"] = 5 }, Now));
            Assert.Null(BuddyStore.Parse(new JsonObject { ["uuid"] = "  " }, Now));
            Assert.Null(BuddyStore.Parse(new JsonObject { ["uuid"] = 7 }, Now));
        }

        [Fact]
        public void AMinimalBuddyReadsWithDefaultsAndAMissingCountingSinceBecomesNow()
        {
            var state = BuddyStore.Parse(new JsonObject { ["uuid"] = "u" }, Now)!;

            Assert.Equal("u", state.Uuid);
            Assert.Equal(Now, state.CountingSince);
            Assert.Equal(0, state.Tokens);
            Assert.Null(state.Name);
            Assert.Empty(state.History);
            Assert.Empty(state.Cursors);
            Assert.Empty(state.RecentMessageIds);
        }

        [Fact]
        public void GarbageValuesCostOnlyThemselves()
        {
            var json = JsonNode.Parse("""
                {
                  "uuid": "u", "rebirths": "two", "tokens": -5, "lifetimeTokens": 1200.0,
                  "stars": 99999999999, "name": 4, "countingSince": "yesterday",
                  "history": [
                    7,
                    { "species": "Wyvern", "rarity": "9", "stage": "Hatchling", "shiny": "yes",
                      "tokens": 1e300, "retiredAt": 5 }
                  ],
                  "ledger": {
                    "files": {
                      "/ok.jsonl": { "offset": 10, "lastTimestamp": "nope", "openMessageOutput": "x" },
                      "/no-offset.jsonl": { "lastCumulative": 4 },
                      "/negative.jsonl": { "offset": -1 },
                      "/text.jsonl": { "offset": "10" },
                      "/scalar.jsonl": 3
                    },
                    "recentIds": [ "a", 5, "", null, "b" ]
                  }
                }
                """)!.AsObject();

            var state = BuddyStore.Parse(json, Now)!;

            Assert.Equal(0, state.Rebirths);
            Assert.Equal(0, state.Tokens);
            Assert.Equal(1200, state.LifetimeTokens);
            Assert.Equal(int.MaxValue, state.Stars);
            Assert.Null(state.Name);
            Assert.Equal(Now, state.CountingSince);

            // One entry per array element, so indices keep lining up with
            // what Save appends after.
            Assert.Equal(2, state.History.Count);
            Assert.Equal("", state.History[0].Name);
            Assert.Equal(BuddySpecies.Duck, state.History[1].Species);
            Assert.Equal(BuddyRarity.Common, state.History[1].Rarity);
            Assert.False(state.History[1].Shiny);
            Assert.Equal(0, state.History[1].Tokens);
            Assert.Equal(DateTimeOffset.MinValue, state.History[1].RetiredAt);

            Assert.Equal(new[] { "/ok.jsonl" }, state.Cursors.Keys);
            Assert.Equal(new LedgerCursor(10, null, null, 0, 0), state.Cursors["/ok.jsonl"]);
            Assert.Equal(new[] { "a", "b" }, state.RecentMessageIds);
        }

        [Fact]
        public void LedgerWithoutFilesOrIdsStillReads()
        {
            var state = BuddyStore.Parse(new JsonObject { ["uuid"] = "u", ["ledger"] = new JsonObject() }, Now)!;

            Assert.Empty(state.Cursors);
            Assert.Empty(state.RecentMessageIds);
        }

        [Fact]
        public void WriteEditsInPlaceAndLeavesUnknownKeysAtEveryDepthAlone()
        {
            var json = JsonNode.Parse("""
                {
                  "uuid": "old", "fromTheFuture": { "keep": true },
                  "history": [ { "rebirth": 0, "name": "First", "species": "Axolotl", "aura": "gold" } ],
                  "ledger": {
                    "cursorVersion": 3,
                    "files": {
                      "/a.jsonl": { "offset": 1, "checksum": "abc" },
                      "/gone.jsonl": { "offset": 5 },
                      "grok://not-a-cursor": { "gauge": 12 }
                    }
                  }
                }
                """)!.AsObject();

            var state = BuddyState.Hatch("new", Now) with
            {
                History = new[]
                {
                    // Index 0 is already on disk and must not be rewritten.
                    new BuddyHistoryEntry(0, "Renamed", BuddySpecies.Duck, BuddyRarity.Common, false,
                        BuddyStage.Hatchling, 0, 0, Now),
                    new BuddyHistoryEntry(1, "Second", BuddySpecies.Owl, BuddyRarity.Epic, false,
                        BuddyStage.Hatchling, 0, 10, Now),
                },
                Cursors = new Dictionary<string, LedgerCursor> { ["/a.jsonl"] = new(99, null, null, 0, 0) }
            };

            BuddyStore.Write(json, state);

            Assert.Equal("new", json["uuid"]!.GetValue<string>());
            Assert.True(json["fromTheFuture"]!["keep"]!.GetValue<bool>());

            var history = json["history"]!.AsArray();
            Assert.Equal(2, history.Count);
            Assert.Equal("First", history[0]!["name"]!.GetValue<string>());
            Assert.Equal("gold", history[0]!["aura"]!.GetValue<string>());
            Assert.Equal("Second", history[1]!["name"]!.GetValue<string>());

            var ledger = json["ledger"]!.AsObject();
            Assert.Equal(3, ledger["cursorVersion"]!.GetValue<int>());
            var files = ledger["files"]!.AsObject();
            Assert.Equal(99, files["/a.jsonl"]!["offset"]!.GetValue<long>());
            Assert.Equal("abc", files["/a.jsonl"]!["checksum"]!.GetValue<string>());

            // A cursor this build wrote and the state dropped goes; an entry
            // that is not a cursor this build understands stays.
            Assert.False(files.ContainsKey("/gone.jsonl"));
            Assert.Equal(12, files["grok://not-a-cursor"]!["gauge"]!.GetValue<int>());
        }

        [Fact]
        public void WriteReplacesSubObjectsThatAreTheWrongShape()
        {
            var json = new JsonObject
            {
                ["history"] = "lost",
                ["ledger"] = new JsonObject { ["files"] = new JsonArray(), ["recentIds"] = "x" }
            };

            BuddyStore.Write(json, Sample());
            Assert.Equal(2, json["history"]!.AsArray().Count);
            Assert.Equal(2, json["ledger"]!["files"]!.AsObject().Count);

            var fresh = new JsonObject { ["ledger"] = 5 };
            BuddyStore.Write(fresh, Sample());
            Assert.Equal(2, fresh["ledger"]!["recentIds"]!.AsArray().Count);

            var scalarFile = new JsonObject
            {
                ["ledger"] = new JsonObject { ["files"] = new JsonObject { ["/a.jsonl"] = 1 } }
            };
            BuddyStore.Write(scalarFile, Sample());
            Assert.Equal(120, scalarFile["ledger"]!["files"]!["/a.jsonl"]!["offset"]!.GetValue<long>());
        }

        // ---- the scanner's pure half ----

        [Theory]
        [InlineData("/p/a.jsonl", "ClaudeCode")]
        [InlineData("/p/s/subagents/agent-1.JSONL", "ClaudeCode")]
        [InlineData("/c/sessions/2026/09/25/rollout-2026-x.jsonl", "Codex")]
        [InlineData("/g/updates.jsonl", null)]
        [InlineData("/p/a.txt", null)]
        public void FormatOfNamesTheDialectOrNothing(string path, string? expected)
        {
            Assert.Equal(expected, BuddyLedgerScanner.FormatOf(path)?.ToString());
        }

        [Fact]
        public void ApplyLedgerMergesCursorsDropsRemovedAndFoldsIds()
        {
            var state = Sample();
            var result = new LedgerScanResult(
                new Dictionary<string, LedgerCursor> { ["/a.jsonl"] = new(500, Now, "msg_9", 3, 0), ["/c.jsonl"] = LedgerCursor.Start },
                new[] { "/rollout-b.jsonl" },
                3,
                new[] { "msg_1", "msg_9" });

            var applied = result.ApplyLedger(state);

            Assert.Equal(new[] { "/a.jsonl", "/c.jsonl" }, applied.Cursors.Keys.OrderBy(k => k));
            Assert.Equal(500, applied.Cursors["/a.jsonl"].Offset);
            Assert.Equal(new[] { "msg_0", "msg_1", "msg_9" }, applied.RecentMessageIds);

            // The token half is BuddyProgress.Credit's; ApplyLedger leaves it.
            Assert.Equal(state.Tokens, applied.Tokens);
            var untouched = LedgerScanResult.Empty.ApplyLedger(state);
            Assert.Equal(state.Cursors, untouched.Cursors);
            Assert.Equal(state.RecentMessageIds, untouched.RecentMessageIds);
        }
    }
}
