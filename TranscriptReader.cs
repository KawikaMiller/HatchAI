using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace HatchAI
{
    // The tail window of a transcript file, and nothing else.
    //
    // Claude Buddy's TranscriptReader also pulls the latest assistant text out
    // for spoken summaries, falls back into ~/.claude/projects to find a lost
    // transcript, and reads Grok's ACP log. The buddy needs none of that: its
    // one reader is ClaudeCliBubbleGenerator, which asks for the last lines of a
    // live session's transcript so BubbleText.LatestUserPrompt can find what
    // the user last typed. The three members below are copied verbatim from
    // there, so "drop the torn first line" behaves exactly as it does in the
    // app the buddy was built and tested in.
    public static class TranscriptReader
    {
        private const int TailBytes = 262144;

        // Excluded from coverage: the try/catch only. The window logic it wraps is
        // in ReadTail below and stays covered — what cannot be arranged is the
        // catch, which is for the file disappearing between the caller's
        // File.Exists and this open. That is a real race, since the file belongs
        // to a session that may be ending, but it is a race and not a state a test
        // can hold still.
        [ExcludeFromCodeCoverage]
        internal static string[] TailLines(string path)
        {
            try { return ReadTail(path, TailBytes); }
            catch { return Array.Empty<string>(); }
        }

        // The same window, sized by the caller.
        [ExcludeFromCodeCoverage]
        internal static string[] TailLines(string path, int tailBytes)
        {
            try { return ReadTail(path, tailBytes); }
            catch { return Array.Empty<string>(); }
        }

        private static string[] ReadTail(string path, int tailBytes)
        {
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long start = Math.Max(0, fs.Length - tailBytes);
                fs.Seek(start, SeekOrigin.Begin);

                using var reader = new StreamReader(fs);
                var chunk = reader.ReadToEnd();

                // If we seeked past the beginning, the first partial line is
                // garbage — drop it.
                if (start > 0)
                {
                    int nl = chunk.IndexOf('\n');
                    if (nl >= 0)
                        chunk = chunk[(nl + 1)..];
                }

                return chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }
    }
}
