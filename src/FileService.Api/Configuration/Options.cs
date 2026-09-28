using System.ComponentModel.DataAnnotations;

namespace FileService.Api.Configuration;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";
    public const string BucketNamePattern = "^[a-z0-9][a-z0-9.-]*[a-z0-9]$";
    public const string KeyPrefixPattern = "^[A-Za-z0-9][A-Za-z0-9/_-]{0,199}$";

    [Required, StringLength(63, MinimumLength = 3), RegularExpression(BucketNamePattern)]
    public string BucketName { get; set; } = string.Empty;

    [Required, RegularExpression("^[a-z]{2}(-[a-z0-9]+)+-[0-9]+$")]
    public string Region { get; set; } = string.Empty;

    // Used only by legacy single-key mode, as the synthesized hooviepack client's prefix.
    [Required, RegularExpression(KeyPrefixPattern)]
    public string KeyPrefix { get; set; } = "files";

    [Range(1, 60)]
    public int UploadUrlLifetimeMinutes { get; set; } = 5;

    [Range(1, 60)]
    public int DownloadUrlLifetimeMinutes { get; set; } = 5;

    [Range(1, long.MaxValue)]
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    public string[] AllowedContentTypes { get; set; } =
        ["image/jpeg", "image/png", "image/webp"];

    // Optional for local S3-compatible testing. Production should use regional Amazon S3.
    public string? ServiceUrl { get; set; }

    public bool ForcePathStyle { get; set; }

    public bool VerifyContentSignature { get; set; } = true;

    public bool CheckBucketOnReady { get; set; }

    public bool PendingCleanupEnabled { get; set; } = true;

    [Range(1, 10080)]
    public int PendingCleanupIntervalMinutes { get; set; } = 60;

    [Range(1, 8760)]
    public int PendingUploadTtlHours { get; set; } = 24;
}

public sealed class InternalApiOptions
{
    public const string SectionName = "InternalApi";

    // Legacy single-key mode only. Validated by ClientRegistry, which also rejects it alongside Clients.
    public string? ApiKey { get; set; }
}

public sealed class ClientOptions
{
    public const string SectionName = "Clients";

    public string Id { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string KeyPrefix { get; set; } = string.Empty;

    public long? MaxFileBytes { get; set; }

    public string[]? AllowedContentTypes { get; set; }

    public string? BucketName { get; set; }
}
