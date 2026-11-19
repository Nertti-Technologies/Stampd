using System.Globalization;
using System.Text;

using static Stampd.Engine.Pades.PdfByteScanner;

namespace Stampd.Engine.Pades;

/// <summary>
/// Builds the objects, xref and trailer of the PAdES document-timestamp revision
/// appended by <see cref="PadesDocTimeStampWriter"/>, and patches the fixed-width
/// <c>/ByteRange</c> and <c>/Contents</c> placeholders once the final offsets and the
/// TST are known.
/// </summary>
internal static class DocTimeStampObjectWriter
{
    // ---- Spec constants ----

    /// <summary>
    /// Reserved hex length for the /Contents placeholder. 16,384 hex chars = 8 KiB of
    /// binary TST. Real-world RFC 3161 TSTs run 2–8 KiB; this gives generous headroom
    /// without bloating the file. PdfSharp uses 8 KiB for the v1.2 archive TST as well.
    /// </summary>
    internal const int ContentsHexLength = 16384;

    /// <summary>
    /// Width of each /ByteRange number as a fixed-width placeholder. 10 chars holds any
    /// signed-32-bit positive offset (up to ~2.1 GB), which is plenty for any PDF a
    /// signing pipeline produces. Right-aligned, space-padded.
    /// </summary>
    private const int ByteRangeNumberWidth = 10;

    // ============================================================================
    // Builders
    // ============================================================================

    /// <summary>
    /// Re-emits the catalog object at its original number, with /AcroForm replaced by
    /// an indirect reference to <paramref name="newAcroFormObjNum"/>. The original
    /// /AcroForm value (whether inline dict, indirect ref, or absent) is stripped so
    /// the catalog has exactly one /AcroForm entry after revision.
    /// </summary>
    internal static byte[] BuildRevisedCatalogObject(int catalogObjNum, byte[] originalDictBody, long newAcroFormObjNum)
    {
        var bodyAscii = Encoding.Latin1.GetString(originalDictBody);

        // Remove any existing /AcroForm entry. Try indirect form first, then inline.
        var indirectPattern = new System.Text.RegularExpressions.Regex(
            @"/AcroForm\s+\d+\s+\d+\s+R\s*");
        if (indirectPattern.IsMatch(bodyAscii))
        {
            bodyAscii = indirectPattern.Replace(bodyAscii, string.Empty, count: 1);
        }
        else
        {
            // Inline — find /AcroForm <<...>> and excise it.
            var keyIdx = bodyAscii.IndexOf("/AcroForm", StringComparison.Ordinal);
            if (keyIdx >= 0)
            {
                // Find the opening << and its matching >>, mirroring ExtractInlineDictAfterKey.
                var cursor = keyIdx + "/AcroForm".Length;
                while (cursor < bodyAscii.Length && IsWhitespace((byte)bodyAscii[cursor])) cursor++;
                if (cursor + 1 < bodyAscii.Length && bodyAscii[cursor] == '<' && bodyAscii[cursor + 1] == '<')
                {
                    var depth = 1;
                    var scan = cursor + 2;
                    while (scan < bodyAscii.Length - 1 && depth > 0)
                    {
                        if (bodyAscii[scan] == '<' && bodyAscii[scan + 1] == '<') { depth++; scan += 2; continue; }
                        if (bodyAscii[scan] == '>' && bodyAscii[scan + 1] == '>') { depth--; scan += 2; continue; }
                        scan++;
                    }
                    // Excise [keyIdx, scan).
                    bodyAscii = bodyAscii[..keyIdx] + bodyAscii[scan..];
                }
            }
        }

        // Append new /AcroForm indirect reference.
        var sb = new StringBuilder();
        sb.Append(catalogObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n<<");
        sb.Append(bodyAscii);
        if (bodyAscii.Length > 0 && !IsWhitespace((byte)bodyAscii[^1]))
        {
            sb.Append('\n');
        }
        sb.Append("/AcroForm ");
        sb.Append(newAcroFormObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 R\n");
        sb.Append(">>\nendobj\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Re-emits the /AcroForm indirect object with the new widget appended to /Fields.
    /// Preserves all other dict entries verbatim — the form may already have /SigFlags,
    /// /DA, /DR, /Q, etc. set by the original signing pass.
    /// </summary>
    internal static byte[] BuildRevisedAcroFormObject(
        long acroFormObjNum,
        byte[] originalDictBody,
        long newFieldRefObjNum)
    {
        var bodyAscii = Encoding.Latin1.GetString(originalDictBody);

        // Patch /Fields [ ... ] to append our new widget ref. PdfSharp emits compact
        // arrays like "/Fields[1 0 R]" or "/Fields [1 0 R 2 0 R]". Regex matches the
        // array and we insert before its ']'.
        var fieldsPattern = new System.Text.RegularExpressions.Regex(
            @"/Fields\s*\[(?<inner>[^\]]*)\]",
            System.Text.RegularExpressions.RegexOptions.None);

        string patchedBody;
        if (fieldsPattern.IsMatch(bodyAscii))
        {
            patchedBody = fieldsPattern.Replace(bodyAscii, match =>
            {
                var inner = match.Groups["inner"].Value.TrimEnd();
                return $"/Fields [{inner} {newFieldRefObjNum.ToString(CultureInfo.InvariantCulture)} 0 R]";
            }, count: 1);
        }
        else
        {
            // No /Fields yet — synthesize one. Defensive against empty input bodies
            // (no original /AcroForm at all). Also add /SigFlags 3 (1=read-only sig
            // appearance + 2=append-only mode) since the form would otherwise lack
            // the basic PAdES form metadata.
            var sigFlagsPart = bodyAscii.Contains("/SigFlags", StringComparison.Ordinal)
                ? string.Empty
                : "\n/SigFlags 3";
            patchedBody = bodyAscii + sigFlagsPart
                + $"\n/Fields [{newFieldRefObjNum.ToString(CultureInfo.InvariantCulture)} 0 R]";
        }

        var sb = new StringBuilder();
        sb.Append(acroFormObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n<<");
        sb.Append(patchedBody);
        if (patchedBody.Length > 0 && !IsWhitespace((byte)patchedBody[^1]))
        {
            sb.Append('\n');
        }
        sb.Append(">>\nendobj\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Invisible widget annotation referring to the signature dictionary. <c>Rect
    /// [0 0 0 0]</c> means zero area — Acrobat won't render any badge for the document
    /// timestamp, which is the expected UX (Document Timestamps are a security feature,
    /// not a visible signature).
    /// </summary>
    internal static byte[] BuildWidgetAnnotation(long widgetObjNum, long sigDictObjNum)
    {
        var sb = new StringBuilder();
        sb.Append(widgetObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n");
        sb.Append("<<\n");
        sb.Append("/Type /Annot\n");
        sb.Append("/Subtype /Widget\n");
        sb.Append("/FT /Sig\n");
        sb.Append("/Rect [0 0 0 0]\n");
        // /T is the field name; PAdES doesn't mandate uniqueness rules beyond per-form
        // distinctness. Use a deterministic prefix + the object number so multiple
        // appends in sequence (future-proofing) don't collide.
        sb.Append("/T (Stampd.DocTimeStamp.");
        sb.Append(widgetObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(")\n");
        sb.Append("/F 132\n"); // Hidden + NoView + Print + Locked flag mix used for invisible sig widgets.
        sb.Append("/V ");
        sb.Append(sigDictObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 R\n");
        sb.Append(">>\nendobj\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds the /DocTimeStamp signature dictionary indirect object with /ByteRange and
    /// /Contents placeholders. Returns the bytes PLUS the in-object offsets of each
    /// placeholder so the caller can compute absolute file offsets and patch them.
    /// </summary>
    internal static (byte[] Bytes, int ByteRangePlaceholderOffset, int ContentsPlaceholderOffset)
        BuildDocTimeStampSigDict(long sigDictObjNum)
    {
        var sb = new StringBuilder();
        sb.Append(sigDictObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n");
        sb.Append("<<\n");
        sb.Append("/Type /DocTimeStamp\n");
        sb.Append("/Filter /Adobe.PPKLite\n");
        sb.Append("/SubFilter /ETSI.RFC3161\n");
        // /V is the version of the signature handler — PAdES doesn't strictly require
        // it on DocTimeStamps but every well-formed example we've seen includes it,
        // and Adobe Reader's strict validator looks for it.
        sb.Append("/V 0\n");

        // /ByteRange placeholder — fixed-width so we can patch numbers in place.
        // The placeholder hash is "/ByteRange [          0          0          0          0]"
        // (4 numbers, each right-aligned in a 10-char field). After patching, the values
        // become the real offsets.
        sb.Append("/ByteRange [");
        var byteRangeStartInSb = sb.Length;
        AppendByteRangePlaceholder(sb, 0); // placeholder #1
        sb.Append(' ');
        AppendByteRangePlaceholder(sb, 0); // placeholder #2
        sb.Append(' ');
        AppendByteRangePlaceholder(sb, 0); // placeholder #3
        sb.Append(' ');
        AppendByteRangePlaceholder(sb, 0); // placeholder #4
        sb.Append("]\n");

        // /Contents = <00...00> zero-filled hex string of length ContentsHexLength.
        sb.Append("/Contents ");
        var contentsStartInSb = sb.Length;
        sb.Append('<');
        sb.Append('0', ContentsHexLength);
        sb.Append('>');
        sb.Append('\n');

        sb.Append(">>\nendobj\n");

        // Convert builder to bytes; the in-object offsets we captured above are by char
        // index, which equals byte index since we're emitting pure Latin-1.
        var bytes = Encoding.Latin1.GetBytes(sb.ToString());
        return (bytes, byteRangeStartInSb, contentsStartInSb);
    }

    private static void AppendByteRangePlaceholder(StringBuilder sb, long value)
    {
        // Right-align in a ByteRangeNumberWidth-char field, space-padded.
        var s = value.ToString(CultureInfo.InvariantCulture);
        sb.Append(' ', ByteRangeNumberWidth - s.Length);
        sb.Append(s);
    }

    internal static byte[] BuildXrefAndTrailer(
        int catalogObjectNumber,
        long catalogOffset,
        long newAcroFormObjNum,
        long newAcroFormOffset,
        long widgetObjNum,
        long widgetOffset,
        long sigDictObjNum,
        long sigDictOffset,
        long previousXrefOffset,
        long newXrefOffset,
        long newSize)
    {
        var sb = new StringBuilder();
        sb.Append("xref\n");

        // Subsection: free head (object 0).
        sb.Append("0 1\n0000000000 65535 f \n");

        // Subsection: revised catalog (single object, same number, new content).
        sb.Append(catalogObjectNumber.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 1\n");
        AppendXrefEntry(sb, catalogOffset);

        // Subsection: new /AcroForm + widget + sig dict, contiguous starting at
        // newAcroFormObjNum. We allocated them sequentially in AppendAsync so a
        // single subsection covers all three.
        sb.Append(newAcroFormObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 3\n");
        AppendXrefEntry(sb, newAcroFormOffset);
        AppendXrefEntry(sb, widgetOffset);
        AppendXrefEntry(sb, sigDictOffset);

        // Trailer.
        sb.Append("trailer\n<<\n");
        sb.Append("/Size ");
        sb.Append(newSize.ToString(CultureInfo.InvariantCulture));
        sb.Append('\n');
        sb.Append("/Prev ");
        sb.Append(previousXrefOffset.ToString(CultureInfo.InvariantCulture));
        sb.Append('\n');
        sb.Append("/Root ");
        sb.Append(catalogObjectNumber.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 R\n");
        sb.Append(">>\n");

        sb.Append("startxref\n");
        sb.Append(newXrefOffset.ToString(CultureInfo.InvariantCulture));
        sb.Append('\n');
        sb.Append("%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static void AppendXrefEntry(StringBuilder sb, long offset)
    {
        sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture));
        sb.Append(" 00000 n \n");
    }

    // ============================================================================
    // Patching helpers — post-construction byte edits
    // ============================================================================

    /// <summary>
    /// Overwrites the four /ByteRange placeholder numbers in place. Each placeholder is
    /// a 10-char space-padded right-aligned ASCII number; we replace it with the actual
    /// value in the same width, keeping the total byte count (and therefore every other
    /// offset) unchanged.
    /// </summary>
    internal static void PatchByteRangeNumbers(
        byte[] buffer,
        long byteRangePlaceholderAbsoluteOffset,
        long v0, long v1, long v2, long v3)
    {
        var p = (int)byteRangePlaceholderAbsoluteOffset;
        WriteByteRangeNumber(buffer, p, v0);
        WriteByteRangeNumber(buffer, p + ByteRangeNumberWidth + 1, v1);
        WriteByteRangeNumber(buffer, p + (ByteRangeNumberWidth + 1) * 2, v2);
        WriteByteRangeNumber(buffer, p + (ByteRangeNumberWidth + 1) * 3, v3);
    }

    private static void WriteByteRangeNumber(byte[] buffer, int offset, long value)
    {
        var s = value.ToString(CultureInfo.InvariantCulture);
        if (s.Length > ByteRangeNumberWidth)
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: /ByteRange value {value} doesn't fit in {ByteRangeNumberWidth} chars.");
        }
        var padLen = ByteRangeNumberWidth - s.Length;
        for (var i = 0; i < padLen; i++) buffer[offset + i] = (byte)' ';
        for (var i = 0; i < s.Length; i++) buffer[offset + padLen + i] = (byte)s[i];
    }

    /// <summary>
    /// Replaces the zero-filled /Contents hex placeholder with the hex encoding of
    /// <paramref name="tstBytes"/>. Trailing hex characters that weren't used remain as
    /// '0' so the placeholder slot stays the same length.
    /// </summary>
    internal static void PatchContentsHex(byte[] buffer, int hexStartOffset, byte[] tstBytes)
    {
        for (var i = 0; i < tstBytes.Length; i++)
        {
            var b = tstBytes[i];
            buffer[hexStartOffset + (i * 2)] = HexNibble(b >> 4);
            buffer[hexStartOffset + (i * 2) + 1] = HexNibble(b & 0x0F);
        }
        // Remaining hex characters keep their '0' fill from initial construction.
    }

    private static byte HexNibble(int v) => (byte)(v < 10 ? '0' + v : 'A' + (v - 10));
}
