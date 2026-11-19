using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;

using Stampd.Core.Sealing;

namespace Stampd.Engine.Sealing;

/// <summary>
/// Adapts an <see cref="ICryptographicSealingProvider"/> to BouncyCastle's
/// <see cref="ISignatureFactory"/> contract so the CMS construction layer can stay
/// provider-agnostic.
/// </summary>
/// <remarks>
/// BouncyCastle's CMS generator drives signing through a synchronous <c>IStreamCalculator</c>
/// abstraction. Our provider is async, so the calculator's <c>GetResult</c> bridges via
/// <c>GetAwaiter().GetResult()</c>. The whole engine pipeline is already running on a
/// thread-pool thread inside <see cref="PdfSharpStampdEngine.SignAsync"/>, so blocking
/// briefly while the provider returns is acceptable. HSM-backed providers can keep their
/// public <see cref="ICryptographicSealingProvider.SignAsync"/> implementations async.
/// </remarks>
internal sealed class ProviderSignatureFactory : ISignatureFactory
{
    private const string RsaKeyOid = "1.2.840.113549.1.1.1";
    private const string EcKeyOid = "1.2.840.10045.2.1";

    private readonly ICryptographicSealingProvider _provider;
    private readonly HashAlgorithmName _hashAlgorithm;
    private readonly AlgorithmIdentifier _signatureAlgorithm;

    public ProviderSignatureFactory(
        ICryptographicSealingProvider provider,
        HashAlgorithmName hashAlgorithm,
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        _provider = provider;
        _hashAlgorithm = hashAlgorithm;
        _signatureAlgorithm = SignatureAlgorithmFor(certificate.GetKeyAlgorithm(), hashAlgorithm);
    }

    public object AlgorithmDetails => _signatureAlgorithm;

    /// <summary>
    /// Builds the CMS signatureAlgorithm from the requested hash and the certificate's
    /// key type. BouncyCastle derives the messageDigest attribute's hash from this value,
    /// so it must name the same hash the provider signs with, or the signature will not
    /// verify.
    /// </summary>
    internal static AlgorithmIdentifier SignatureAlgorithmFor(string keyAlgorithmOid, HashAlgorithmName hashAlgorithm)
        => (keyAlgorithmOid, hashAlgorithm.Name) switch
        {
            // RFC 4055 §5: RSA PKCS#1 v1.5 identifiers carry an explicit NULL parameter.
            (RsaKeyOid, "SHA256") => new AlgorithmIdentifier(PkcsObjectIdentifiers.Sha256WithRsaEncryption, DerNull.Instance),
            (RsaKeyOid, "SHA384") => new AlgorithmIdentifier(PkcsObjectIdentifiers.Sha384WithRsaEncryption, DerNull.Instance),
            (RsaKeyOid, "SHA512") => new AlgorithmIdentifier(PkcsObjectIdentifiers.Sha512WithRsaEncryption, DerNull.Instance),
            // RFC 5758 §3.2: ECDSA identifiers omit the parameters field.
            (EcKeyOid, "SHA256") => new AlgorithmIdentifier(X9ObjectIdentifiers.ECDsaWithSha256),
            (EcKeyOid, "SHA384") => new AlgorithmIdentifier(X9ObjectIdentifiers.ECDsaWithSha384),
            (EcKeyOid, "SHA512") => new AlgorithmIdentifier(X9ObjectIdentifiers.ECDsaWithSha512),
            _ => throw new NotSupportedException(
                $"Sealing does not support key algorithm '{keyAlgorithmOid}' with hash '{hashAlgorithm.Name}'."),
        };

    public IStreamCalculator<IBlockResult> CreateCalculator()
        => new ProviderStreamCalculator(_provider, _hashAlgorithm);

    private sealed class ProviderStreamCalculator : IStreamCalculator<IBlockResult>, IDisposable
    {
        private readonly MemoryStream _buffer = new();
        private readonly ICryptographicSealingProvider _provider;
        private readonly HashAlgorithmName _hashAlgorithm;
        private bool _disposed;

        public ProviderStreamCalculator(ICryptographicSealingProvider provider, HashAlgorithmName hashAlgorithm)
        {
            _provider = provider;
            _hashAlgorithm = hashAlgorithm;
        }

        public Stream Stream => _buffer;

        public IBlockResult GetResult()
        {
            var data = _buffer.ToArray();
            var signature = _provider
                .SignAsync(data, _hashAlgorithm, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return new ByteArrayBlockResult(signature);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _buffer.Dispose();
            _disposed = true;
        }
    }

    private sealed class ByteArrayBlockResult : IBlockResult
    {
        private readonly byte[] _bytes;

        public ByteArrayBlockResult(byte[] bytes) => _bytes = bytes;

        public byte[] Collect() => _bytes;

        public int Collect(byte[] destination, int offset)
        {
            Buffer.BlockCopy(_bytes, 0, destination, offset, _bytes.Length);
            return _bytes.Length;
        }

        public int Collect(Span<byte> destination)
        {
            _bytes.AsSpan().CopyTo(destination);
            return _bytes.Length;
        }

        public int GetMaxResultLength() => _bytes.Length;
    }
}
