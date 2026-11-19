using System.Globalization;
using System.Text;

namespace Stampd.Engine.Pades;

/// <summary>
/// Byte-level scanning primitives for the document-timestamp path
/// (<see cref="PadesDocTimeStampWriter"/>, <see cref="PdfRevisionReader"/> and
/// <see cref="DocTimeStampObjectWriter"/>): keyword search, whitespace and line-end
/// skipping, string skipping and ASCII integer parsing.
/// </summary>
internal static class PdfByteScanner
{
    internal static long SkipLiteralString(byte[] pdf, long openOffset)
    {
        var depth = 1;
        var cursor = openOffset + 1;
        while (cursor < pdf.Length && depth > 0)
        {
            var b = pdf[(int)cursor];
            if (b == (byte)'\\') { cursor += 2; continue; }
            if (b == (byte)'(') depth++;
            else if (b == (byte)')') depth--;
            cursor++;
        }
        return cursor;
    }

    internal static long SkipHexString(byte[] pdf, long openOffset)
    {
        var cursor = openOffset + 1;
        while (cursor < pdf.Length && pdf[(int)cursor] != (byte)'>') cursor++;
        return cursor + 1;
    }

    // ---- Tiny scanner primitives ----

    internal static bool NeedsLeadingNewline(byte[] pdf)
    {
        if (pdf.Length == 0) return false;
        var last = pdf[^1];
        return last != (byte)'\n' && last != (byte)'\r';
    }

    internal static int LastIndexOf(byte[] haystack, byte[] needle, int start, int length)
    {
        for (var i = start + length - needle.Length; i >= start; i--)
        {
            if (MatchesAt(haystack, i, needle)) return i;
        }
        return -1;
    }

    internal static int IndexOf(byte[] haystack, byte[] needle, long start, long length)
    {
        var end = start + length - needle.Length;
        for (var i = start; i <= end; i++)
        {
            if (MatchesAt(haystack, (int)i, needle)) return (int)i;
        }
        return -1;
    }

    private static bool MatchesAt(byte[] haystack, int offset, byte[] needle)
    {
        if (offset + needle.Length > haystack.Length) return false;
        for (var i = 0; i < needle.Length; i++)
        {
            if (haystack[offset + i] != needle[i]) return false;
        }
        return true;
    }

    internal static bool StartsWithAt(byte[] haystack, long offset, ReadOnlySpan<byte> needle)
    {
        if (offset < 0 || offset + needle.Length > haystack.Length) return false;
        for (var i = 0; i < needle.Length; i++)
        {
            if (haystack[(int)(offset + i)] != needle[i]) return false;
        }
        return true;
    }

    internal static long SkipWhitespace(byte[] pdf, long cursor)
    {
        while (cursor < pdf.Length && IsWhitespace(pdf[(int)cursor])) cursor++;
        return cursor;
    }

    internal static long SkipToLineEnd(byte[] pdf, long cursor)
    {
        while (cursor < pdf.Length && pdf[(int)cursor] != (byte)'\n' && pdf[(int)cursor] != (byte)'\r') cursor++;
        return cursor;
    }

    internal static long SkipLineEnd(byte[] pdf, long cursor)
    {
        if (cursor < pdf.Length && pdf[(int)cursor] == (byte)'\r') cursor++;
        if (cursor < pdf.Length && pdf[(int)cursor] == (byte)'\n') cursor++;
        return cursor;
    }

    internal static bool IsWhitespace(byte b) =>
        b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == 0 || b == 0x0C;

    internal static (long Value, long AfterCursor) ReadAsciiInteger(byte[] pdf, long cursor)
    {
        var start = cursor;
        while (cursor < pdf.Length && pdf[(int)cursor] >= (byte)'0' && pdf[(int)cursor] <= (byte)'9') cursor++;
        if (cursor == start)
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: expected digit at offset {start}.");
        }
        var digits = AsciiSubstring(pdf, start, (int)(cursor - start));
        return (long.Parse(digits, CultureInfo.InvariantCulture), cursor);
    }

    internal static string AsciiSubstring(byte[] pdf, long offset, int length)
        => Encoding.Latin1.GetString(pdf, (int)offset, length);
}
