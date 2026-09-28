using System.Globalization;

namespace HatchAI
{
    // "1.23M", "12.5k", "950" — for the card's token and lifetime lines.
    // Owned by E3 (CB-195). Invariant culture: a comma decimal separator in a
    // number this short reads as a thousands separator.
    //
    // Three significant digits, truncated rather than rounded, and that is the
    // one decision in here worth defending. The card puts this number next to
    // the next milestone ("2.49k / 2.5k"), and an egg at 2,499 tokens has not
    // hatched. Rounding would print "2.5k" beside an egg — a claim about a
    // milestone reached that the sprite beside it contradicts. Truncating can
    // only ever under-state, by less than one in the last digit shown.
    //
    // Trailing zeros go ("10k", not "10.0k") because milestones are round
    // numbers and the card prints several of them.
    internal static class TokenFormat
    {
        private static readonly (long Unit, string Suffix)[] Units =
        {
            (1_000_000_000_000, "T"),
            (1_000_000_000, "B"),
            (1_000_000, "M"),
            (1_000, "k"),
        };

        internal static string Compact(long tokens)
        {
            // Nothing earns negative tokens, but a formatter should not be the
            // thing that throws about it. long.MinValue has no positive twin,
            // so it is widened through decimal rather than negated as a long.
            if (tokens < 0) return "-" + Scaled(-(decimal)tokens);

            return Scaled(tokens);
        }

        private static string Scaled(decimal tokens)
        {
            foreach (var (unit, suffix) in Units)
            {
                if (tokens < unit) continue;

                var value = tokens / unit;

                // 1.23 / 12.3 / 123 — two, one or no decimals, so the figure
                // is three digits wide whatever its magnitude. Past 999T it
                // simply grows; nothing counts that high.
                var scale = value < 10 ? 100m : value < 100 ? 10m : 1m;
                var truncated = Math.Floor(value * scale) / scale;

                return truncated.ToString("0.##", CultureInfo.InvariantCulture) + suffix;
            }

            return tokens.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
