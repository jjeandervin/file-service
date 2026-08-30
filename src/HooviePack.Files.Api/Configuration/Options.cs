using System.ComponentModel.DataAnnotations;

namespace HooviePack.Files.Api.Configuration;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    [Required, StringLength(63, MinimumLength = 3), RegularExpression("^[a-z0-9][a-z0-9.-]*[a-z0-9]$")]
    public string BucketName { get; set; } = string.Empty;

    [Required, RegularExpression("^[a-z]{2}(-[a-z0-9]+)+-[0-9]+$")]
    public string Region { get; set; } = string.Empty;

    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9/_-]{0,199}$")]
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
}

public sealed class InternalApiOptions
{
    public const string SectionName = "InternalApi";

    [Required, MinLength(32)]
    public string ApiKey { get; set; } = string.Empty;
}
