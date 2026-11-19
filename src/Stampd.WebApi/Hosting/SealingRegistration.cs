using Stampd.Crypto.AwsKms;
using Stampd.Crypto.AzureKeyVault;
using Stampd.Crypto.LocalCertificate;
using Stampd.Crypto.Vault;

namespace Stampd.WebApi.Hosting;

internal static class SealingRegistration
{
    /// <summary>
    /// Registers the configured <c>ICryptographicSealingProvider</c>. The local-certificate
    /// fallback uses <paramref name="certPath"/> / <paramref name="certPassword"/>, which
    /// Program.cs reads from configuration before any registration.
    /// </summary>
    /// <remarks>
    /// Ordering invariant: the engine factory (EngineRegistration.AddStampdEngine) resolves
    /// the sealing provider registered here; Program.cs calls this before the engine.
    /// </remarks>
    public static void AddStampdSealing(
        this WebApplicationBuilder builder, string certPath, string certPassword)
    {
        // Sealing: LocalCertificate, Vault, or AzureKeyVault by config.
        var sealingProvider = builder.Configuration["Stampd:Sealing:Provider"] ?? "Local";
        if (string.Equals(sealingProvider, "Vault", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddVaultSealing(options =>
            {
                options.VaultAddress = new Uri(builder.Configuration["Stampd:Sealing:Vault:Address"]
                    ?? throw new InvalidOperationException("Vault:Address required"));

                // Auth method selection: Token (default, dev), AppRole (production VMs/CI),
                // Kubernetes (production K8s workloads). VaultSharp handles lease renewal
                // automatically for AppRole and Kubernetes.
                var authMethod = builder.Configuration["Stampd:Sealing:Vault:AuthMethod"] ?? "Token";
                options.AuthMethod = Enum.Parse<VaultAuthMethod>(authMethod, ignoreCase: true);
                options.Token = builder.Configuration["Stampd:Sealing:Vault:Token"];
                options.AppRoleId = builder.Configuration["Stampd:Sealing:Vault:AppRoleId"];
                options.AppRoleSecretId = builder.Configuration["Stampd:Sealing:Vault:AppRoleSecretId"];
                options.AppRoleMountPath = builder.Configuration["Stampd:Sealing:Vault:AppRoleMountPath"]
                    ?? "approle";
                options.KubernetesRole = builder.Configuration["Stampd:Sealing:Vault:KubernetesRole"];
                options.KubernetesServiceAccountTokenPath =
                    builder.Configuration["Stampd:Sealing:Vault:KubernetesServiceAccountTokenPath"]
                    ?? "/var/run/secrets/kubernetes.io/serviceaccount/token";
                options.KubernetesMountPath = builder.Configuration["Stampd:Sealing:Vault:KubernetesMountPath"]
                    ?? "kubernetes";

                options.TransitMountPath = builder.Configuration["Stampd:Sealing:Vault:TransitMountPath"] ?? "transit";
                options.KeyName = builder.Configuration["Stampd:Sealing:Vault:KeyName"];
                options.CertificatePath = builder.Configuration["Stampd:Sealing:Vault:CertificatePath"];
            });
        }
        else if (string.Equals(sealingProvider, "AzureKeyVault", StringComparison.OrdinalIgnoreCase))
        {
            // Azure Key Vault: uses DefaultAzureCredential which chains environment vars,
            // Managed Identity, Azure CLI, etc. The signing key never leaves the vault; the
            // cert (public part) is downloaded once and cached.
            builder.Services.AddAzureKeyVaultSealing(options =>
            {
                options.KeyIdentifier = new Uri(builder.Configuration["Stampd:Sealing:AzureKeyVault:KeyIdentifier"]
                    ?? throw new InvalidOperationException("AzureKeyVault:KeyIdentifier required"));
                options.CertificateVaultUri = new Uri(builder.Configuration["Stampd:Sealing:AzureKeyVault:CertificateVaultUri"]
                    ?? throw new InvalidOperationException("AzureKeyVault:CertificateVaultUri required"));
                options.CertificateName = builder.Configuration["Stampd:Sealing:AzureKeyVault:CertificateName"]
                    ?? throw new InvalidOperationException("AzureKeyVault:CertificateName required");
            });
        }
        else if (string.Equals(sealingProvider, "AwsKms", StringComparison.OrdinalIgnoreCase))
        {
            // AWS KMS: auth via the AWS SDK default credential chain (env vars, shared profile,
            // EC2 instance metadata, ECS task role, EKS IRSA / Pod Identity). The cert (public
            // part) lives on disk alongside the running service; KMS holds the private key.
            builder.Services.AddAwsKmsSealing(options =>
            {
                options.KeyId = builder.Configuration["Stampd:Sealing:AwsKms:KeyId"]
                    ?? throw new InvalidOperationException("AwsKms:KeyId required");
                options.Region = builder.Configuration["Stampd:Sealing:AwsKms:Region"];
                options.CertificatePath = builder.Configuration["Stampd:Sealing:AwsKms:CertificatePath"]
                    ?? throw new InvalidOperationException("AwsKms:CertificatePath required");
            });
        }
        else
        {
            builder.Services.AddLocalCertificateSealing(_ =>
            {
                var (cert, _) = SelfSignedCertificateFactory.CreateOrLoad(certPath, certPassword);
                return cert;
            });
        }
    }
}
