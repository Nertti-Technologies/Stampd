using System.Security.Cryptography.X509Certificates;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Revocation;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine.Rendering;
using Stampd.Engine.Tests.Internal;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// The engine reports the PAdES level it actually reached, falling back from the target
/// when a timestamp authority or revocation evidence is missing, and never above it.
/// </summary>
public sealed class AchievedLevelTests
{
    public enum Revocation
    {
        None,
        CertificatesOnly,
        WithResponses,
    }

    [Theory]
    [InlineData(PAdESLevel.BB, true, Revocation.WithResponses, PAdESLevel.BB)]
    [InlineData(PAdESLevel.BT, false, Revocation.None, PAdESLevel.BB)]
    [InlineData(PAdESLevel.BT, true, Revocation.None, PAdESLevel.BT)]
    [InlineData(PAdESLevel.BT, true, Revocation.WithResponses, PAdESLevel.BT)]
    [InlineData(PAdESLevel.BLT, false, Revocation.WithResponses, PAdESLevel.BB)]
    [InlineData(PAdESLevel.BLT, true, Revocation.None, PAdESLevel.BT)]
    [InlineData(PAdESLevel.BLT, true, Revocation.CertificatesOnly, PAdESLevel.BT)]
    [InlineData(PAdESLevel.BLT, true, Revocation.WithResponses, PAdESLevel.BLT)]
    [InlineData(PAdESLevel.BLTA, true, Revocation.CertificatesOnly, PAdESLevel.BT)]
    [InlineData(PAdESLevel.BLTA, true, Revocation.WithResponses, PAdESLevel.BLTA)]
    public async Task Engine_ReportsTheLevelItReached(PAdESLevel target, bool hasTsa, Revocation revocation, PAdESLevel expected)
    {
        PlatformFontResolver.Register();
        using var certificate = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd Achieved Level Signer",
            validity: TimeSpan.FromDays(30));

        var engine = new PdfSharpStampdEngine(
            new LocalCertificateSealingProvider(certificate),
            timestampAuthority: hasTsa ? new PadesBLtaTests.InProcessStubTsaProvider() : null,
            revocationProvider: revocation switch
            {
                Revocation.CertificatesOnly => new FixedRevocationProvider(new CertificateRevocationInfo(null, null)),
                Revocation.WithResponses => new FixedRevocationProvider(
                    new CertificateRevocationInfo("ocsp"u8.ToArray(), "crl"u8.ToArray())),
                _ => null,
            });

        var signed = await engine.SignAsync(
            new SignatureRequest
            {
                SourcePdf = SampleAssetFactory.BuildSourcePdf(),
                Fields = [],
                FieldValues = new Dictionary<int, ReadOnlyMemory<byte>>(),
                Sealing = new SealingOptions { TargetLevel = target },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, signed.AchievedLevel);
    }

    private sealed class FixedRevocationProvider(CertificateRevocationInfo info) : IRevocationProvider
    {
        public string Name => "fixed";

        public Task<CertificateRevocationInfo> GetRevocationInfoAsync(
            X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default)
            => Task.FromResult(info);
    }
}
