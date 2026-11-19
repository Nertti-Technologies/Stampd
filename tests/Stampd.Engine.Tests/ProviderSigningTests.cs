using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Security;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Sealing;
using Stampd.Engine.Rendering;
using Stampd.Engine.Sealing;
using Stampd.Engine.Tests.Internal;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// Covers the provider signing path: a certificate without a local private key, so the
/// engine signs through <see cref="ICryptographicSealingProvider.SignAsync"/>, as it does
/// for key vaults and HSMs. The CMS must declare the algorithm the provider actually used.
/// </summary>
public sealed class ProviderSigningTests
{
    [Theory]
    [InlineData("SHA-256")]
    [InlineData("SHA-384")]
    [InlineData("SHA-512")]
    public async Task RsaProvider_SignatureVerifiesForEachDigest(string digest)
    {
        using var rsa = RSA.Create(2048);
        using var provider = new PublicOnlyProvider(rsa, CreateCertificate(rsa));

        var inspector = await SignAsync(provider, digest);

        Assert.True(VerifyDetached(inspector, provider.PublicCertificate));
        Assert.Equal(DigestOid(digest), inspector.FirstSigner.DigestAlgorithmID.Algorithm.Id);
    }

    [Theory]
    [InlineData("SHA-256")]
    [InlineData("SHA-384")]
    public async Task EcdsaProvider_SignatureVerifiesAndIsLabelledEcdsa(string digest)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = new PublicOnlyProvider(ecdsa, CreateCertificate(ecdsa));

        var inspector = await SignAsync(provider, digest);

        Assert.True(VerifyDetached(inspector, provider.PublicCertificate));
        var expected = digest == "SHA-256" ? X9ObjectIdentifiers.ECDsaWithSha256 : X9ObjectIdentifiers.ECDsaWithSha384;
        Assert.Equal(expected.Id, inspector.FirstSigner.SignatureAlgorithm.Algorithm.Id);
    }

    [Fact]
    public void SignatureAlgorithmFor_RejectsUnsupportedKeyType()
    {
        // id-Ed25519: not supported by the sealing path, so it must fail loudly rather
        // than be mislabelled.
        Assert.Throws<NotSupportedException>(
            () => ProviderSignatureFactory.SignatureAlgorithmFor("1.3.101.112", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void SignatureAlgorithmFor_RsaCarriesNullParameters()
    {
        var identifier = ProviderSignatureFactory.SignatureAlgorithmFor("1.2.840.113549.1.1.1", HashAlgorithmName.SHA384);

        Assert.Equal(PkcsObjectIdentifiers.Sha384WithRsaEncryption, identifier.Algorithm);
        Assert.Equal(Org.BouncyCastle.Asn1.DerNull.Instance, identifier.Parameters);
    }

    private static async Task<SignedPdfInspector> SignAsync(ICryptographicSealingProvider provider, string digest)
    {
        PlatformFontResolver.Register();

        var engine = new PdfSharpStampdEngine(provider, timestampAuthority: null, revocationProvider: null);
        var request = new SignatureRequest
        {
            SourcePdf = SampleAssetFactory.BuildSourcePdf(),
            Fields =
            [
                new SignatureField(
                    PageNumber: 1,
                    Bounds: new PercentageRect(X: 10, Y: 80, Width: 30, Height: 4),
                    Kind: SignatureFieldKind.Text,
                    SignerId: "test-signer"),
            ],
            FieldValues = new Dictionary<int, ReadOnlyMemory<byte>>
            {
                [0] = "Signed through a provider"u8.ToArray(),
            },
            Sealing = new SealingOptions { TargetLevel = PAdESLevel.BB, DigestAlgorithm = digest },
            Metadata = new SignatureMetadata(Reason: "provider path test"),
        };

        var signed = await engine.SignAsync(request, TestContext.Current.CancellationToken);
        return SignedPdfInspector.Parse(signed.SignedPdf.ToArray());
    }

    /// <summary>
    /// The PAdES CMS is detached, so verification needs the /ByteRange bytes supplied
    /// as the signed content.
    /// </summary>
    private static bool VerifyDetached(SignedPdfInspector inspector, X509Certificate2 certificate)
    {
        var withContent = new CmsSignedData(new CmsProcessableByteArray(inspector.ByteRangeBytes()), inspector.CmsBytes);
        var signer = withContent.GetSignerInfos().GetSigners().First();
        return signer.Verify(DotNetUtilities.FromX509Certificate(certificate));
    }

    private static string DigestOid(string digest) => digest switch
    {
        "SHA-256" => "2.16.840.1.101.3.4.2.1",
        "SHA-384" => "2.16.840.1.101.3.4.2.2",
        "SHA-512" => "2.16.840.1.101.3.4.2.3",
        _ => throw new ArgumentOutOfRangeException(nameof(digest)),
    };

    private static X509Certificate2 CreateCertificate(RSA rsa)
        => new CertificateRequest("CN=Stampd Provider Test (RSA)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

    private static X509Certificate2 CreateCertificate(ECDsa ecdsa)
        => new CertificateRequest("CN=Stampd Provider Test (ECDSA)", ecdsa, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

    /// <summary>
    /// Keeps the private key to itself and hands the engine a public-only certificate,
    /// the way a key vault or HSM provider does.
    /// </summary>
    private sealed class PublicOnlyProvider : ICryptographicSealingProvider, IDisposable
    {
        private readonly AsymmetricAlgorithm _key;

        public PublicOnlyProvider(AsymmetricAlgorithm key, X509Certificate2 certificateWithKey)
        {
            _key = key;
            using (certificateWithKey)
            {
                PublicCertificate = X509CertificateLoader.LoadCertificate(certificateWithKey.RawData);
            }
        }

        public X509Certificate2 PublicCertificate { get; }

        public string Name => "PublicOnlyTest";

        public Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(PublicCertificate);

        public Task<byte[]> SignAsync(byte[] data, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken = default)
            => Task.FromResult(_key switch
            {
                RSA rsa => rsa.SignData(data, hashAlgorithm, RSASignaturePadding.Pkcs1),
                // The provider contract returns ECDSA signatures as a DER (r, s) sequence.
                ECDsa ecdsa => ecdsa.SignData(data, hashAlgorithm, DSASignatureFormat.Rfc3279DerSequence),
                _ => throw new NotSupportedException(),
            });

        public void Dispose() => PublicCertificate.Dispose();
    }
}
