using System.Security.Cryptography;

using Stampd.Core.Sealing;

using static Stampd.Engine.Pades.DocTimeStampObjectWriter;
using static Stampd.Engine.Pades.PdfByteScanner;
using static Stampd.Engine.Pades.PdfRevisionReader;

namespace Stampd.Engine.Pades;

/// <summary>
/// v1.3 #132 — appends a PAdES <i>Document Timestamp</i> (PDF /DocTimeStamp signature
/// dictionary) to a finished signed PDF as a strictly additive incremental update revision,
/// per ETSI EN 319 142-1 §5.4 (PAdES baseline B-LTA conformance level).
/// </summary>
/// <remarks>
/// <para>
/// A Document Timestamp is structurally a second signature on the document — but instead
/// of signing user data, it carries an RFC 3161 timestamp token over the entire prior PDF
/// (including the original B-T signature AND the DSS revocation material appended by
/// <see cref="PadesIncrementalUpdateWriter"/>). This anchors everything — signature,
/// timestamp, revocation info, document content — to a fresh TSA assertion, so the
/// composite stays verifiable even after the original signer and TSA certificates expire.
/// </para>
/// <para>
/// <b>Why Document Timestamps over CMS archive-time-stamp-v3.</b> v1.2 implemented B-LTA
/// as an <c>id-aa-ets-archiveTimestampV3</c> CMS unsigned attribute on the original
/// signer (Part 2 era; v1.3 #131 made the imprint strict). ETSI EN 319 142-1 Part 4 §5.4
/// explicitly defines B-LTA conformance via <i>Document Timestamps</i> instead — a
/// separate PDF signature with <c>/Type /DocTimeStamp</c> and <c>/SubFilter
/// /ETSI.RFC3161</c>. Stampd now emits BOTH: belt-and-suspenders Part 2 + Part 4 coverage
/// at the cost of one extra TSA round-trip per B-LTA signing.
/// </para>
/// <para>
/// <b>Wire format.</b> The appended revision contains:
/// </para>
/// <list type="bullet">
///   <item>A revised <c>/AcroForm</c> indirect object whose <c>/Fields</c> array gains a
///   reference to the new document-timestamp signature widget.</item>
///   <item>An invisible signature widget annotation (<c>/Subtype /Widget /FT /Sig /Rect
///   [0 0 0 0]</c>) whose <c>/V</c> entry points at the signature dict below.</item>
///   <item>A signature dictionary with <c>/Type /DocTimeStamp</c>, <c>/Filter
///   /Adobe.PPKLite</c>, <c>/SubFilter /ETSI.RFC3161</c>, a four-segment <c>/ByteRange</c>
///   covering everything except the <c>/Contents</c> value, and <c>/Contents</c> filled
///   with the RFC 3161 TST DER hex-encoded.</item>
/// </list>
/// <para>
/// <b>Implementation strategy.</b> Same byte-level approach as
/// <see cref="PadesIncrementalUpdateWriter"/>: no PdfSharp on the write path (it would
/// re-serialize and invalidate the original signature). We build the revision with
/// fixed-width placeholders for <c>/ByteRange</c> numbers and a zero-filled hex
/// placeholder for <c>/Contents</c>, then patch both in place after computing the
/// ByteRange digest and obtaining the TST from the TSA.
/// </para>
/// <para>
/// <b>Code layout.</b> This class keeps the orchestration in <see cref="AppendAsync"/>.
/// Reading the previous revision lives in <see cref="PdfRevisionReader"/>, byte-level
/// scanning in <see cref="PdfByteScanner"/>, and building the appended objects plus
/// placeholder patching in <see cref="DocTimeStampObjectWriter"/>.
/// </para>
/// </remarks>
internal static class PadesDocTimeStampWriter
{
    /// <summary>
    /// Builds the document-timestamp revision, hashes the ByteRange-covered bytes, calls
    /// the TSA, patches the placeholder with the returned TST DER, and returns the full
    /// (original + appended) bytes.
    /// </summary>
    /// <param name="pdfBytes">
    /// The signed PDF, optionally already extended with a DSS revision by
    /// <see cref="PadesIncrementalUpdateWriter"/>. The Document Timestamp covers
    /// everything in here.
    /// </param>
    /// <param name="tsa">RFC 3161 TSA provider that will sign the ByteRange digest.</param>
    /// <param name="hashAlgorithm">
    /// Hash to use for the ByteRange digest. Same algorithm the engine uses for the
    /// rest of the signature so the verifier doesn't have to juggle hash agility.
    /// </param>
    /// <param name="ct">Cancellation token plumbed through to the TSA call.</param>
    public static async Task<byte[]> AppendAsync(
        byte[] pdfBytes,
        ITimestampAuthorityProvider tsa,
        HashAlgorithmName hashAlgorithm,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(tsa);

        // ---- 1. Parse the existing PDF structure ----
        var previousXrefOffset = FindPreviousXrefOffset(pdfBytes);
        var (catalogObjectNumber, previousSize) = ReadTrailerRootAndSize(pdfBytes, previousXrefOffset);
        var catalogOffset = ReadObjectOffsetFromXref(pdfBytes, previousXrefOffset, catalogObjectNumber);
        var catalogDictBody = ReadObjectDictBody(pdfBytes, catalogOffset, catalogObjectNumber);

        // /AcroForm in PdfSharp 6.x output can be either indirect (`/AcroForm N N R`)
        // or inline (`/AcroForm <<...>>`). Resolve to the dict body bytes either way
        // — we always emit a new indirect /AcroForm and revise the catalog to point
        // at it, so the original AcroForm gets orphaned (harmless under PDF
        // incremental-update semantics: /Prev xref still finds the old version but
        // catalog -> AcroForm follows the new chain).
        var existingAcroFormBody = ResolveAcroFormBody(pdfBytes, previousXrefOffset, catalogDictBody);

        // ---- 2. Allocate object numbers ----
        // newAcroFormObjNum: holds the revised /AcroForm dict (new indirect object)
        // widgetObjNum:      holds the invisible /DocTimeStamp widget annotation
        // sigDictObjNum:     holds the actual /DocTimeStamp signature dict (sig field /V)
        // catalog is revised IN PLACE at catalogObjectNumber (same number, new content)
        var newAcroFormObjNum = previousSize;
        var widgetObjNum = previousSize + 1;
        var sigDictObjNum = previousSize + 2;

        // ---- 3. Build the new objects ----

        using var appended = new MemoryStream();
        var leadingNewline = NeedsLeadingNewline(pdfBytes);
        if (leadingNewline)
        {
            appended.WriteByte((byte)'\n');
        }

        var appendedRegionStart = pdfBytes.Length + (leadingNewline ? 1 : 0);

        // Revised catalog: re-emits the catalog at its original object number with
        // /AcroForm replaced by an indirect reference to our new AcroForm object.
        // This handles both inline-form catalogs (where /AcroForm <<...>> existed) and
        // indirect-form catalogs (where /AcroForm pointed to a now-orphaned object).
        var revisedCatalogOffset = appendedRegionStart + (int)appended.Position;
        var revisedCatalog = BuildRevisedCatalogObject(
            catalogObjectNumber,
            catalogDictBody,
            newAcroFormObjNum: newAcroFormObjNum);
        appended.Write(revisedCatalog, 0, revisedCatalog.Length);

        // New /AcroForm indirect object: contains the existing dict body (with /Fields
        // array patched to include our new widget) plus /SigFlags 3 if not already set.
        var revisedAcroFormOffset = appendedRegionStart + (int)appended.Position;
        var revisedAcroForm = BuildRevisedAcroFormObject(
            newAcroFormObjNum,
            existingAcroFormBody,
            newFieldRefObjNum: widgetObjNum);
        appended.Write(revisedAcroForm, 0, revisedAcroForm.Length);

        // Widget annotation (invisible — Rect [0 0 0 0]). Refers to the sig dict via /V.
        var widgetOffset = appendedRegionStart + (int)appended.Position;
        var widget = BuildWidgetAnnotation(widgetObjNum, sigDictObjNum);
        appended.Write(widget, 0, widget.Length);

        // Signature dict — this is the /DocTimeStamp. Built with placeholders for
        // /ByteRange numbers and /Contents hex. We capture the absolute file offsets
        // of those placeholders so we can patch them post-construction.
        var sigDictOffsetInFinal = appendedRegionStart + (int)appended.Position;
        var (sigDictBytes, byteRangePlaceholderOffsetInSigDict, contentsPlaceholderOffsetInSigDict) =
            BuildDocTimeStampSigDict(sigDictObjNum);
        appended.Write(sigDictBytes, 0, sigDictBytes.Length);

        // ---- 4. Append xref + trailer + EOF ----

        var newXrefOffset = appendedRegionStart + (int)appended.Position;
        var newSize = sigDictObjNum + 1;

        var xrefAndTrailer = BuildXrefAndTrailer(
            catalogObjectNumber: catalogObjectNumber,
            catalogOffset: revisedCatalogOffset,
            newAcroFormObjNum: newAcroFormObjNum,
            newAcroFormOffset: revisedAcroFormOffset,
            widgetObjNum: widgetObjNum,
            widgetOffset: widgetOffset,
            sigDictObjNum: sigDictObjNum,
            sigDictOffset: sigDictOffsetInFinal,
            previousXrefOffset: previousXrefOffset,
            newXrefOffset: newXrefOffset,
            newSize: newSize);
        appended.Write(xrefAndTrailer, 0, xrefAndTrailer.Length);

        // ---- 5. Concatenate original + appended ----
        var combined = new byte[pdfBytes.Length + (int)appended.Length];
        Buffer.BlockCopy(pdfBytes, 0, combined, 0, pdfBytes.Length);
        var appendedBytes = appended.ToArray();
        Buffer.BlockCopy(appendedBytes, 0, combined, pdfBytes.Length, appendedBytes.Length);

        // ---- 6. Patch /ByteRange numbers ----

        // The /Contents value occupies <reserved hex> from offset C_start to C_end.
        // /ByteRange = [0 length1 C_end length2] where:
        //   length1 = bytes [0..C_start), i.e., everything BEFORE the < of /Contents
        //   length2 = bytes [C_end..EOF), i.e., everything AFTER the > of /Contents
        // C_start is the absolute file offset of the '<' opening the /Contents value.
        // C_end is the absolute file offset just past the '>' closing it.
        var sigDictAbsoluteOffset = pdfBytes.Length + (leadingNewline ? 1 : 0) +
            (sigDictOffsetInFinal - appendedRegionStart);
        var contentsStartInFile = sigDictAbsoluteOffset + contentsPlaceholderOffsetInSigDict;
        // Placeholder hex sits between '<' and '>'; ContentsHexLength counts only the
        // hex characters, so the closing '>' is at contentsStart + 1 + ContentsHexLength.
        var contentsEndInFile = contentsStartInFile + 1 + ContentsHexLength + 1; // include both delimiters
        // /ByteRange entries cover bytes BEFORE the '<' and AFTER the '>'.
        var byteRange0 = 0L;
        var byteRange1 = contentsStartInFile;
        var byteRange2 = contentsEndInFile;
        var byteRange3 = combined.Length - contentsEndInFile;

        PatchByteRangeNumbers(
            combined,
            byteRangePlaceholderAbsoluteOffset: sigDictAbsoluteOffset + byteRangePlaceholderOffsetInSigDict,
            byteRange0, byteRange1, byteRange2, byteRange3);

        // ---- 7. Compute the ByteRange digest, call TSA, patch /Contents ----

        using var hasher = CreateHasher(hashAlgorithm);
        hasher.TransformBlock(combined, 0, byteRange1, null, 0);
        hasher.TransformFinalBlock(combined, byteRange2, byteRange3);
        //var digest = hasher.Hash!;

        // The TSA contract: pass the bytes to be timestamped; the provider hashes them
        // with hashAlgorithm and wraps the digest in an RFC 3161 TimeStampReq. So we
        // need to hand it bytes that, when hashed, produce `digest`. Since we already
        // have `digest`, we'd need to either (a) pass `digest` as raw bytes (the TSA
        // would then double-hash) or (b) re-hand the concatenated bytes. Re-handing is
        // semantically cleaner and matches the existing CMS-archive-TST path.
        var byteRangeBytes = ConcatenateByteRange(combined, byteRange1, byteRange2, byteRange3);
        var tstBytes = await tsa
            .RequestTimestampAsync(byteRangeBytes, hashAlgorithm, ct)
            .ConfigureAwait(false);

        if (tstBytes.Length * 2 > ContentsHexLength)
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: TST is {tstBytes.Length} bytes; placeholder reserves "
                + $"{ContentsHexLength / 2} bytes. Increase ContentsHexLength.");
        }

        PatchContentsHex(combined, contentsStartInFile + 1, tstBytes);

        return combined;
    }

    private static byte[] ConcatenateByteRange(byte[] source, long len1, long start2, long len2)
    {
        var result = new byte[len1 + len2];
        Buffer.BlockCopy(source, 0, result, 0, (int)len1);
        Buffer.BlockCopy(source, (int)start2, result, (int)len1, (int)len2);
        return result;
    }

    private static HashAlgorithm CreateHasher(HashAlgorithmName algo) => algo.Name switch
    {
        "SHA256" => SHA256.Create(),
        "SHA384" => SHA384.Create(),
        "SHA512" => SHA512.Create(),
        _ => throw new NotSupportedException($"Unsupported hash algorithm: {algo.Name}"),
    };
}
