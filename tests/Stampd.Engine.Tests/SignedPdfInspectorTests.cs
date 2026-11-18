using Stampd.Engine.Tests.Internal;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// Guards the test helper that pulls the CMS out of a signed PDF's zero-padded /Contents.
/// The real signature ends in a 0x00 byte about once in 256 signings, which the old
/// trailing-zero trimming silently cut off.
/// </summary>
public sealed class SignedPdfInspectorTests
{
    [Fact]
    public void TakeDerObject_KeepsTrailingZeroByteOfTheObject()
    {
        // SEQUENCE { INTEGER 0 } = 30 03 02 01 00, followed by placeholder padding.
        byte[] padded = [0x30, 0x03, 0x02, 0x01, 0x00, 0x00, 0x00, 0x00];

        var der = SignedPdfInspector.TakeDerObject(padded);

        Assert.Equal(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x00 }, der);
    }

    [Fact]
    public void TakeDerObject_ReadsLongFormLength()
    {
        // SEQUENCE with a two-octet length (0x0102 = 258 content bytes), ending in 0x00.
        var padded = new byte[4 + 258 + 16];
        padded[0] = 0x30;
        padded[1] = 0x82;
        padded[2] = 0x01;
        padded[3] = 0x02;
        padded[4] = 0xAB;

        var der = SignedPdfInspector.TakeDerObject(padded);

        Assert.Equal(4 + 258, der.Length);
        Assert.Equal(0xAB, der[4]);
    }

    [Fact]
    public void TakeDerObject_RejectsLengthBeyondPlaceholder()
    {
        byte[] padded = [0x30, 0x05, 0x02, 0x01];

        Assert.Throws<InvalidOperationException>(() => SignedPdfInspector.TakeDerObject(padded));
    }

    [Fact]
    public void TakeDerObject_RejectsIndefiniteLength()
    {
        byte[] padded = [0x30, 0x80, 0x02, 0x01, 0x00, 0x00, 0x00];

        Assert.Throws<InvalidOperationException>(() => SignedPdfInspector.TakeDerObject(padded));
    }
}
