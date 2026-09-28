namespace FileService.Contracts;

public sealed record DownloadResponse(
    Guid FileId,
    string DownloadUrl,
    DateTimeOffset ExpiresAt);
