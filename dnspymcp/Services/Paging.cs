namespace DnSpyMcp.Services;

/// <summary>
/// Small helpers so every tool clamps its output the same way. Keeps the LLM
/// context tight — a runaway list or a 10-MB decompile should never reach the
/// client by default.
/// </summary>
internal static class Paging
{
    // Default page sizes are intentionally small so the common case doesn't
    // blow up the LLM context. Tools may request larger pages, but *never*
    // exceed HardMaxRows / HardMaxChars — a single tool call cannot pull the
    // whole thing down, the caller has to page.
    public const int DefaultMaxRows = 100;
    public const int HardMaxRows    = 500;
    public const int DefaultMaxChars = 32_000;    // ~8k tokens
    public const int HardMaxChars    = 128_000;   // ~32k tokens
    public const int DefaultMaxBytesHex = 1 << 20; // 1 MiB raw

    /// <summary>Apply offset + max to a sequence and wrap with paging metadata.</summary>
    public static object Page<T>(IEnumerable<T> source, int offset, int max)
    {
        if (offset < 0) offset = 0;
        if (max <= 0) max = DefaultMaxRows;
        if (max > HardMaxRows) max = HardMaxRows;

        var all = source as IList<T> ?? source.ToList();
        var total = all.Count;
        var page = all.Skip(offset).Take(max).ToList();
        var end = offset + page.Count;
        var truncated = end < total;
        return new
        {
            total,
            offset,
            returned = page.Count,
            truncated,
            nextOffset = truncated ? (int?)end : null,
            hint = truncated
                ? $"truncated: {page.Count} of {total} rows returned; pass offset={end} to continue"
                : null,
            items = page,
        };
    }

    /// <summary>Apply head/tail style windowing to a big string payload.</summary>
    public static object ClampText(string text, int offsetChars, int maxChars)
    {
        if (text == null) text = string.Empty;
        if (offsetChars < 0) offsetChars = 0;
        if (maxChars <= 0) maxChars = DefaultMaxChars;
        if (maxChars > HardMaxChars) maxChars = HardMaxChars;

        var totalChars = text.Length;
        if (offsetChars >= totalChars)
        {
            return new
            {
                totalChars,
                offsetChars,
                returnedChars = 0,
                truncated = false,
                nextOffsetChars = (int?)null,
                hint = (string?)null,
                text = string.Empty,
            };
        }
        var slice = totalChars - offsetChars <= maxChars
            ? text.Substring(offsetChars)
            : text.Substring(offsetChars, maxChars);
        var end = offsetChars + slice.Length;
        var truncated = end < totalChars;
        return new
        {
            totalChars,
            offsetChars,
            returnedChars = slice.Length,
            truncated,
            nextOffsetChars = truncated ? (int?)end : null,
            hint = truncated
                ? $"truncated: {slice.Length} of {totalChars} chars returned; pass offsetChars={end} to continue"
                : null,
            text = slice,
        };
    }
}
