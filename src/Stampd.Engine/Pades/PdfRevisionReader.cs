using System.Globalization;
using System.Text;

using static Stampd.Engine.Pades.PdfByteScanner;

namespace Stampd.Engine.Pades;

/// <summary>
/// Reads the previous revision of a classic-xref PDF for
/// <see cref="PadesDocTimeStampWriter"/>: locates the last xref, reads the trailer's
/// <c>/Root</c> and <c>/Size</c>, resolves object offsets through the xref chain and
/// returns object dictionary bodies (including the catalog's <c>/AcroForm</c>).
/// </summary>
internal static class PdfRevisionReader
{
    // ---- Shared keyword buffers (avoid per-call allocation) ----

    private static readonly byte[] StartxrefKeyword = "startxref"u8.ToArray();
    private static readonly byte[] TrailerKeyword = "trailer"u8.ToArray();
    private static readonly byte[] XrefKeyword = "xref"u8.ToArray();
    private static readonly byte[] DictOpen = "<<"u8.ToArray();

    // ============================================================================
    // PDF parsing helpers — pared down from PadesIncrementalUpdateWriter for reuse.
    // Kept separate from that writer so the two can evolve independently; consolidation
    // into a shared PadesPdfBytes utility class is a v2.0 cleanup.
    // ============================================================================

    /// <summary>
    /// Resolves the existing /AcroForm dict body bytes from the catalog. Handles both
    /// indirect (<c>/AcroForm N N R</c>) and inline (<c>/AcroForm &lt;&lt;...&gt;&gt;</c>)
    /// forms. Returns an empty body when no /AcroForm exists — the caller synthesizes
    /// a fresh one with just our new field.
    /// </summary>
    internal static byte[] ResolveAcroFormBody(byte[] pdf, long previousXrefOffset, byte[] catalogDictBody)
    {
        // Try indirect first — most common when the original signing pass created an
        // /AcroForm via PdfSharp's Internals.AddObject.
        var indirect = FindIndirectReferenceInDict(catalogDictBody, "/AcroForm");
        if (indirect is not null)
        {
            var offset = ReadObjectOffsetFromXref(pdf, previousXrefOffset, indirect.Value);
            return ReadObjectDictBody(pdf, offset, indirect.Value);
        }

        // Try inline. PdfSharp 6.x is known to emit `/AcroForm<< ... >>` directly in the
        // catalog body. We scan for the key, then for the opening `<<` that follows it,
        // then walk to the matching `>>` using a nesting-aware scanner so an inline
        // /AcroForm dict containing nested dicts (the typical /DR, /Fields nesting) is
        // captured in full rather than truncated at the first `>>`.
        var inlineBody = ExtractInlineDictAfterKey(catalogDictBody, "/AcroForm");
        if (inlineBody is not null)
        {
            return inlineBody;
        }

        // No /AcroForm at all — return empty body so BuildRevisedAcroFormObject
        // synthesizes a minimal one. Rare in practice because the original signing
        // pass always creates an AcroForm, but defensive against synthetic test PDFs.
        return [];
    }

    /// <summary>
    /// Walks <paramref name="dictBody"/> looking for <paramref name="key"/> followed by
    /// a <c>&lt;&lt;</c>, then returns the bytes between that <c>&lt;&lt;</c> and its
    /// matching <c>&gt;&gt;</c>. Returns null if the key isn't found or isn't followed
    /// by an inline dict.
    /// </summary>
    private static byte[]? ExtractInlineDictAfterKey(byte[] dictBody, string key)
    {
        var bodyAscii = Encoding.Latin1.GetString(dictBody);
        var keyIdx = bodyAscii.IndexOf(key, StringComparison.Ordinal);
        if (keyIdx < 0) return null;

        // Skip whitespace after the key.
        var cursor = keyIdx + key.Length;
        while (cursor < bodyAscii.Length && IsWhitespace((byte)bodyAscii[cursor])) cursor++;

        if (cursor + 1 >= bodyAscii.Length || bodyAscii[cursor] != '<' || bodyAscii[cursor + 1] != '<')
        {
            return null;
        }

        // Walk forward looking for matching >>. Use a depth counter to handle nested
        // dicts inside the inline AcroForm (it almost certainly contains /DR <<...>>).
        var depth = 1;
        var scan = cursor + 2;
        while (scan < bodyAscii.Length - 1)
        {
            // Skip literal/hex strings to avoid mistaking embedded '<' or '>' for dict
            // delimiters — same defensive scanning as FindMatchingDictClose.
            var ch = bodyAscii[scan];
            if (ch == '(') { scan = (int)SkipLiteralString(dictBody, scan); continue; }
            if (ch == '<' && bodyAscii[scan + 1] != '<')
            {
                scan = (int)SkipHexString(dictBody, scan);
                continue;
            }
            if (ch == '<' && bodyAscii[scan + 1] == '<') { depth++; scan += 2; continue; }
            if (ch == '>' && bodyAscii[scan + 1] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    var bodyStart = cursor + 2;
                    var bodyLength = scan - bodyStart;
                    var body = new byte[bodyLength];
                    Buffer.BlockCopy(dictBody, bodyStart, body, 0, bodyLength);
                    return body;
                }
                scan += 2;
                continue;
            }
            scan++;
        }
        return null;
    }

    internal static long FindPreviousXrefOffset(byte[] pdf)
    {
        var scanStart = Math.Max(0, pdf.Length - 1024);
        var keywordIdx = LastIndexOf(pdf, StartxrefKeyword, scanStart, pdf.Length - scanStart);
        if (keywordIdx < 0)
        {
            throw new InvalidOperationException(
                "PadesDocTimeStampWriter: input PDF has no 'startxref' marker in the last 1 KB.");
        }
        long cursor = keywordIdx + StartxrefKeyword.Length;
        cursor = SkipWhitespace(pdf, cursor);
        var (offset, _) = ReadAsciiInteger(pdf, cursor);
        return offset;
    }

    internal static (int CatalogObjectNumber, int Size) ReadTrailerRootAndSize(byte[] pdf, long previousXrefOffset)
    {
        var trailerKeywordIdx = IndexOf(pdf, TrailerKeyword, previousXrefOffset, pdf.Length - previousXrefOffset);
        if (trailerKeywordIdx < 0)
        {
            throw new InvalidOperationException(
                "PadesDocTimeStampWriter: no 'trailer' keyword found after xref. "
                + "Hybrid xref-stream PDFs are not supported.");
        }

        var dictOpenIdx = IndexOf(pdf, DictOpen, trailerKeywordIdx, pdf.Length - trailerKeywordIdx);
        var dictCloseIdx = FindMatchingDictClose(pdf, dictOpenIdx);
        var dictBody = AsciiSubstring(pdf, dictOpenIdx + 2, (int)(dictCloseIdx - (dictOpenIdx + 2)));

        var rootMatch = System.Text.RegularExpressions.Regex.Match(dictBody, @"/Root\s+(\d+)\s+\d+\s+R");
        var sizeMatch = System.Text.RegularExpressions.Regex.Match(dictBody, @"/Size\s+(\d+)");

        if (!rootMatch.Success || !sizeMatch.Success)
        {
            throw new InvalidOperationException(
                "PadesDocTimeStampWriter: trailer missing /Root or /Size.");
        }
        return (
            int.Parse(rootMatch.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    internal static long ReadObjectOffsetFromXref(byte[] pdf, long xrefOffset, int objectNumber)
    {
        if (!StartsWithAt(pdf, xrefOffset, XrefKeyword))
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: expected 'xref' keyword at offset {xrefOffset}.");
        }

        var cursor = xrefOffset + XrefKeyword.Length;
        cursor = SkipWhitespace(pdf, cursor);

        // Chain back through /Prev xrefs if needed. For our test inputs the object is
        // usually in the latest xref, but if it was set in an earlier revision (e.g.
        // /AcroForm created by the original signing pass and not touched since), we'll
        // need to follow /Prev. The lookup recurses, so it follows /Prev through any
        // number of earlier revisions.
        while (cursor < pdf.Length)
        {
            if (StartsWithAt(pdf, cursor, TrailerKeyword))
            {
                // Try /Prev one hop back.
                var (_, prevOpt) = ReadPrevFromTrailer(pdf, cursor);
                if (prevOpt is not null)
                {
                    return ReadObjectOffsetFromXref(pdf, prevOpt.Value, objectNumber);
                }
                throw new InvalidOperationException(
                    $"PadesDocTimeStampWriter: object {objectNumber} not found in xref chain.");
            }

            var (firstObj, afterFirst) = ReadAsciiInteger(pdf, cursor);
            cursor = SkipWhitespace(pdf, afterFirst);
            var (count, afterCount) = ReadAsciiInteger(pdf, cursor);
            cursor = SkipToLineEnd(pdf, afterCount);
            cursor = SkipLineEnd(pdf, cursor);

            for (var i = 0; i < count; i++)
            {
                var entryStart = cursor;
                var objNum = firstObj + i;
                if (objNum == objectNumber)
                {
                    var offsetStr = AsciiSubstring(pdf, entryStart, 10);
                    var inUseChar = (char)pdf[(int)(entryStart + 17)];
                    if (inUseChar == 'n')
                    {
                        return long.Parse(offsetStr, CultureInfo.InvariantCulture);
                    }
                }
                cursor += 20;
            }
        }

        throw new InvalidOperationException(
            $"PadesDocTimeStampWriter: object {objectNumber} not found in xref table.");
    }

    private static (string DictBody, long? PrevOffset) ReadPrevFromTrailer(byte[] pdf, long trailerOffset)
    {
        var dictOpenIdx = IndexOf(pdf, DictOpen, trailerOffset, pdf.Length - trailerOffset);
        var dictCloseIdx = FindMatchingDictClose(pdf, dictOpenIdx);
        var body = AsciiSubstring(pdf, dictOpenIdx + 2, (int)(dictCloseIdx - (dictOpenIdx + 2)));
        var prevMatch = System.Text.RegularExpressions.Regex.Match(body, @"/Prev\s+(\d+)");
        if (!prevMatch.Success) return (body, null);
        return (body, long.Parse(prevMatch.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    internal static byte[] ReadObjectDictBody(byte[] pdf, long objectOffset, int expectedObjNum)
    {
        var (objNum, afterObjNum) = ReadAsciiInteger(pdf, objectOffset);
        if (objNum != expectedObjNum)
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: object at offset {objectOffset} has number {objNum}, "
                + $"expected {expectedObjNum}.");
        }

        var cursor = SkipWhitespace(pdf, afterObjNum);
        var (_, afterGen) = ReadAsciiInteger(pdf, cursor);
        cursor = SkipWhitespace(pdf, afterGen);

        if (!StartsWithAt(pdf, cursor, "obj"u8))
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: object {expectedObjNum} header is malformed.");
        }
        cursor += 3;
        cursor = SkipWhitespace(pdf, cursor);

        if (!StartsWithAt(pdf, cursor, DictOpen))
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: object {expectedObjNum} body does not begin with '<<'.");
        }

        var dictOpenIdx = cursor;
        var dictCloseIdx = FindMatchingDictClose(pdf, dictOpenIdx);
        var bodyStart = dictOpenIdx + 2;
        var bodyLength = (int)(dictCloseIdx - bodyStart);
        var body = new byte[bodyLength];
        Buffer.BlockCopy(pdf, (int)bodyStart, body, 0, bodyLength);
        return body;
    }

    private static int? FindIndirectReferenceInDict(byte[] dictBody, string key)
    {
        var bodyAscii = Encoding.Latin1.GetString(dictBody);
        var pattern = $@"{System.Text.RegularExpressions.Regex.Escape(key)}\s+(\d+)\s+\d+\s+R";
        var match = System.Text.RegularExpressions.Regex.Match(bodyAscii, pattern);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static long FindMatchingDictClose(byte[] pdf, long openOffset)
    {
        var depth = 1;
        var cursor = openOffset + 2;
        while (cursor < pdf.Length - 1)
        {
            var b = pdf[(int)cursor];
            if (b == (byte)'(')
            {
                cursor = SkipLiteralString(pdf, cursor);
                continue;
            }
            if (b == (byte)'<' && cursor + 1 < pdf.Length && pdf[(int)(cursor + 1)] != (byte)'<')
            {
                cursor = SkipHexString(pdf, cursor);
                continue;
            }
            if (pdf[(int)cursor] == (byte)'<' && pdf[(int)(cursor + 1)] == (byte)'<')
            {
                depth++; cursor += 2; continue;
            }
            if (pdf[(int)cursor] == (byte)'>' && pdf[(int)(cursor + 1)] == (byte)'>')
            {
                depth--;
                if (depth == 0) return cursor;
                cursor += 2; continue;
            }
            cursor++;
        }
        throw new InvalidOperationException(
            $"PadesDocTimeStampWriter: unmatched '<<' at offset {openOffset}.");
    }
}
