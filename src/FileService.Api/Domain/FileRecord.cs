namespace FileService.Api.Domain;

public enum FileStatus
{
    Pending,
    Ready
}

public sealed class FileRecord
{
    // Owner of rows created before clients existed, and the database default for ClientId. The default is
    // permanent: pre-extraction HooviePack images insert rows without ClientId, so rolling back still works.
    public const string DefaultClientId = "hooviepack";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string ClientId { get; set; }
    public required string StorageKey { get; set; }

    // The bucket the object was written to. Null means the global FileStorage:BucketName (rows from before clients).
    public string? BucketName { get; set; }
    public required string OriginalFileName { get; set; }
    public required string ContentType { get; set; }
    public long DeclaredSize { get; set; }
    public long? ActualSize { get; set; }
    public required byte[] UploadTokenHash { get; set; }
    public string? LegacySourcePath { get; set; }
    public FileStatus Status { get; set; } = FileStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UploadedAt { get; set; }
}
