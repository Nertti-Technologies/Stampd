using Stampd.Infrastructure.Sqlite;
using Stampd.Storage.AzureBlob;
using Stampd.Storage.FileSystem;
using Stampd.Storage.Gcs;
using Stampd.Storage.S3;

namespace Stampd.WebApi.Hosting;

internal static class StorageRegistration
{
    /// <summary>
    /// Registers the SQLite persistence and the configured document-storage provider.
    /// <paramref name="sqlitePath"/> and <paramref name="storageRoot"/> are read by Program.cs
    /// before any registration (and their directories created there), so callers keep that
    /// read point unchanged.
    /// </summary>
    public static void AddStampdPersistenceAndStorage(
        this WebApplicationBuilder builder, string sqlitePath, string storageRoot)
    {
        // Persistence
        builder.Services.AddStampdSqlite($"Data Source={sqlitePath}");

        // Document storage: FileSystem (default, dev), S3, AzureBlob, or GCS.
        var storageProvider = builder.Configuration["Stampd:Storage:Provider"] ?? "FileSystem";
        if (string.Equals(storageProvider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddS3DocumentStorage(options =>
            {
                options.BucketName = builder.Configuration["Stampd:Storage:S3:BucketName"]
                    ?? throw new InvalidOperationException("Storage:S3:BucketName required");
                options.Region = builder.Configuration["Stampd:Storage:S3:Region"];
                options.KeyPrefix = builder.Configuration["Stampd:Storage:S3:KeyPrefix"];
                options.KmsKeyId = builder.Configuration["Stampd:Storage:S3:KmsKeyId"];
                var serviceUrl = builder.Configuration["Stampd:Storage:S3:ServiceUrl"];
                if (!string.IsNullOrEmpty(serviceUrl))
                {
                    options.ServiceUrl = new Uri(serviceUrl);
                }
            });
        }
        else if (string.Equals(storageProvider, "AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddAzureBlobDocumentStorage(options =>
            {
                options.AccountUri = new Uri(builder.Configuration["Stampd:Storage:AzureBlob:AccountUri"]
                    ?? throw new InvalidOperationException("Storage:AzureBlob:AccountUri required"));
                options.ContainerName = builder.Configuration["Stampd:Storage:AzureBlob:ContainerName"]
                    ?? throw new InvalidOperationException("Storage:AzureBlob:ContainerName required");
                options.KeyPrefix = builder.Configuration["Stampd:Storage:AzureBlob:KeyPrefix"];
                options.EncryptionScope = builder.Configuration["Stampd:Storage:AzureBlob:EncryptionScope"];
            });
        }
        else if (string.Equals(storageProvider, "GCS", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddGcsDocumentStorage(options =>
            {
                options.BucketName = builder.Configuration["Stampd:Storage:Gcs:BucketName"]
                    ?? throw new InvalidOperationException("Storage:Gcs:BucketName required");
                options.KeyPrefix = builder.Configuration["Stampd:Storage:Gcs:KeyPrefix"];
                options.KmsKeyName = builder.Configuration["Stampd:Storage:Gcs:KmsKeyName"];
            });
        }
        else
        {
            builder.Services.AddFileSystemDocumentStorage(storageRoot);
        }
    }
}
