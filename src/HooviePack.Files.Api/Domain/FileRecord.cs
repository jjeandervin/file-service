namespace HooviePack.Files.Api.Domain;

public enum FileStatus
{
    Pending,
    Ready
}

public sealed class FileRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string StorageKey { get; set; }
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
