using System.Text.Json;
using System.Text.RegularExpressions;

namespace HatchAI
{
    // Turning what Claude Code writes down into turns.
    //
    // Ported from Claude Buddy, where it also fed the chat panel. HatchAI uses
    // it only for the buddy's AI-bubble prompt (BubbleText.LatestUserPrompt),
    // so the half that read a permission dialog off a terminal pane was left
    // behind: nothing here has a terminal to read.
    //
    // Pure on purpose: no files, no windows, no settings, no dispatcher — just
    // text in and turns out, so it can be tested without opening anything.
    //
    // Map() reads the transcript, which is a documented-by-example append-only
    // JSONL file. Unfamiliar rows are skipped, so a Claude Code upgrade that
    // adds a row type degrades rather than breaks.
    public static class ChatTranscript
    {
        public readonly record struct Row(string? Uuid, ChatTurn Turn);

        // --- the transcript ---

        public static List<Row> Map(IEnumerable<string> lines)
        {
            var mapped = new List<Row>();

            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] != '{') continue;

                // Parsing every row would mean parsing the ones that carry the
                // bytes: a file-history snapshot or an attachment is routinely
                // larger than the whole conversation around it, and none of the
                // types skipped here can produce a turn. A substring test rather
                // than a parse for the same reason TranscriptReader uses one —
                // the point is to not build a JsonDocument for a megabyte that
                // is about to be discarded.
                if (!IsInteresting(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    MapRow(doc.RootElement, mapped);
                }
                catch
                {
                    // A row we can't parse is a row we don't show.
                }
            }

            return mapped;
        }

        // A false positive costs nothing: a tool result whose text happens to
        // contain one of these parses to no turns anyway, because it has no text
        // blocks of its own. A false negative would silently drop a message, so
        // the test that matters is that these three strings are exactly the row
        // types MapRow handles.
        public static bool IsInteresting(string line) =>
            line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"queue-operation\"", StringComparison.Ordinal);

        private static void MapRow(JsonElement root, List<Row> into)
        {
            var type = Str(root, "type");
            if (type is null) return;

            // Subagent chatter. One team run produces thousands of these and
            // they would bury the conversation the panel was opened to read.
            if (Bool(root, "isSidechain")) return;

            var uuid = Str(root, "uuid");
            var at = Time(root);

            switch (type)
            {
                case "user":
                    MapUser(root, uuid, at, into);
                    return;

                case "assistant":
                    MapAssistant(root, uuid, at, into);
                    return;

                // Sent while the session was busy. Claude Code holds it and
                // picks it up at the end of the turn, and showing that is the
                // difference between "did that send?" and knowing it did.
                case "queue-operation" when Str(root, "operation") == "enqueue":
                {
                    var queued = Str(root, "content");
                    if (string.IsNullOrWhiteSpace(queued) || IsNoise(queued)) return;

                    into.Add(new Row(uuid, new ChatTurn
                    {
                        Role = ChatRole.User,
                        Text = queued.Trim(),
                        IsComplete = true,
                        At = at
                    }));
                    return;
                }
            }
        }

        private static void MapUser(JsonElement root, string? uuid, DateTimeOffset at, List<Row> into)
        {
            // Rows Claude Code writes to itself — hook output, command
            // scaffolding, the transcript's own bookkeeping.
            if (Bool(root, "isMeta")) return;
            if (!root.TryGetProperty("message", out var message)) return;
            if (!message.TryGetProperty("content", out var content)) return;

            if (content.ValueKind == JsonValueKind.String)
            {
                AddText(content.GetString(), ChatRole.User, uuid, at, into);
                return;
            }

            if (content.ValueKind != JsonValueKind.Array) return;

            // A pasted picture arrives as two sibling blocks in one message —
            // its own "image" block, and a "text" block carrying Claude
            // Code's own "[Image #1]" placeholder for wherever its own UI
            // would draw the picture inline. Confirmed against a real
            // transcript row, not assumed: pasting a picture through this
            // panel and typing its path (see LocalCliChatSession) produced
            // exactly this shape. Whether Codex's rollout format matches is
            // unverified — CodexTranscript's own Map is untouched, so a
            // picture in a Codex turn still shows as whatever text Codex
            // wrote, with no thumbnail.
            //
            // Only the first image is kept: a turn carries one, the same
            // limit a received picture already has.
            byte[]? image = null;
            var textBlocks = new List<string?>();

            foreach (var block in content.EnumerateArray())
            {
                var kind = Str(block, "type");

                if (kind == "image")
                {
                    image ??= DecodeImage(block);
                    continue;
                }

                // tool_result blocks are the other half of a tool_use that is
                // already one line in the panel. Rendering them would put the
                // contents of every file read into the conversation.
                if (kind != "text") continue;

                textBlocks.Add(Str(block, "text"));
            }

            if (textBlocks.Count == 0)
            {
                // A picture with nothing typed alongside it is still a turn
                // worth showing — just with no caption.
                if (image is not null)
                {
                    into.Add(new Row(uuid, new ChatTurn
                    {
                        Role = ChatRole.User,
                        Text = "",
                        IsComplete = true,
                        At = at,
                        ImageBytes = image
                    }));
                }

                return;
            }

            for (var i = 0; i < textBlocks.Count; i++)
            {
                // The placeholder for the picture sits in the last text
                // block found in the real row this was measured against, so
                // that is where the decoded picture rides too.
                var carries = image is not null && i == textBlocks.Count - 1;
                AddText(textBlocks[i], ChatRole.User, uuid, at, into, carries ? image : null);
            }
        }

        // Claude Code writes "[Image #1]", "[Image #2]" and so on directly
        // into the text block beside a pasted picture's own block — a
        // placeholder for wherever its own UI draws the picture inline. This
        // app draws the picture itself right there in the bubble, so once one
        // is actually attached the placeholder is only noise. Leading only:
        // the real row this was measured against had it at the front, and
        // there is nothing to say it is always there.
        private static readonly Regex ImagePlaceholder = new(@"^(\[Image #\d+\]\s*)+", RegexOptions.Compiled);

        // A picture's own bytes, already inline as base64 — unlike
        // OpenClawSessions.DecodeDataUri, which decodes a single "data:…"
        // URI string, a transcript's image block already carries "type" and
        // "data" as separate fields.
        private static byte[]? DecodeImage(JsonElement block)
        {
            if (!block.TryGetProperty("source", out var source)) return null;
            if (Str(source, "type") != "base64") return null;

            var data = Str(source, "data");
            if (string.IsNullOrEmpty(data)) return null;

            try { return Convert.FromBase64String(data); }
            catch { return null; }
        }

        private static void MapAssistant(JsonElement root, string? uuid, DateTimeOffset at, List<Row> into)
        {
            if (!root.TryGetProperty("message", out var message)) return;
            if (!message.TryGetProperty("content", out var content)) return;
            if (content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                switch (Str(block, "type"))
                {
                    case "text":
                        AddText(Str(block, "text"), ChatRole.Assistant, uuid, at, into);
                        break;

                    // Shown, and as its own turn rather than folded into the
                    // reply — the same call OpenClawChatSession makes, for the
                    // same reason: watching a session think is most of the value
                    // of an orb that pulses.
                    case "thinking":
                        AddText(Str(block, "thinking"), ChatRole.System, uuid, at, into);
                        break;

                    case "tool_use":
                        into.Add(new Row(uuid, new ChatTurn
                        {
                            Role = ChatRole.System,
                            Text = ToolSummary(block),
                            IsComplete = true,
                            At = at
                        }));
                        break;
                }
            }
        }

        private static void AddText(
            string? text, ChatRole role, string? uuid, DateTimeOffset at, List<Row> into, byte[]? imageBytes = null)
        {
            var shown = imageBytes is null ? text : ImagePlaceholder.Replace(text ?? "", "");

            // A caption-less picture still deserves a turn, so the emptiness
            // check has to run on the text after the placeholder is
            // stripped — otherwise a picture whose only text was that
            // placeholder would vanish along with it.
            if (imageBytes is null)
            {
                if (string.IsNullOrWhiteSpace(shown)) return;
                if (role == ChatRole.User && IsNoise(shown!)) return;
            }
            else if (role == ChatRole.User && !string.IsNullOrWhiteSpace(shown) && IsNoise(shown!))
            {
                return;
            }

            into.Add(new Row(uuid, new ChatTurn
            {
                Role = role,
                Text = (shown ?? "").Trim(),
                IsComplete = true,
                At = at,
                ImageBytes = imageBytes
            }));
        }

        // Things that arrive as user turns but were never said by the user:
        // injected context, slash-command scaffolding, notifications from
        // background work. All of it is addressed to the model, none of it to a
        // person reading the conversation back.
        private static readonly string[] NoisePrefixes =
        {
            "<system-reminder>",
            "<command-name>",
            "<command-message>",
            "<command-args>",
            "<local-command-stdout>",
            "<local-command-caveat>",
            "<task-notification>",
            "<user-prompt-submit-hook>",
            "[Request interrupted",
            "Caveat: The messages below"
        };

        public static bool IsNoise(string text)
        {
            var t = text.TrimStart();
            foreach (var prefix in NoisePrefixes)
                if (t.StartsWith(prefix, StringComparison.Ordinal)) return true;

            return false;
        }

        // "· Read OrbArrangement.cs" — the tool, and the one argument saying
        // which thing it touched. A bare tool name is nearly useless in a column
        // of nine of them, and the whole input is a paragraph.
        private static readonly string[] ArgKeys =
        {
            "file_path", "notebook_path", "path", "command", "pattern", "url", "query", "description", "prompt"
        };

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        public static string ToolSummary(JsonElement block)
        {
            var name = Str(block, "name") ?? "tool";

            if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                return "· " + name;

            foreach (var key in ArgKeys)
            {
                if (!input.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind != JsonValueKind.String) continue;

                var arg = value.GetString();
                if (string.IsNullOrWhiteSpace(arg)) continue;

                if (key is "file_path" or "notebook_path" or "path") arg = Path.GetFileName(arg);

                arg = Whitespace.Replace(arg.Trim(), " ");
                if (arg.Length > 48) arg = arg[..48] + "…";

                return "· " + name + "  " + arg;
            }

            return "· " + name;
        }

        // --- json helpers ---

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.True;

        private static DateTimeOffset Time(JsonElement e) =>
            Str(e, "timestamp") is { } s && DateTimeOffset.TryParse(s, out var at)
                ? at.ToLocalTime()
                : DateTimeOffset.Now;
    }
}
