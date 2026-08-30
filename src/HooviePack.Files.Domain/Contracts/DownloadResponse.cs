namespace HooviePack.Files.Domain;

public sealed record DownloadResponse(
    Guid FileId,
    string DownloadUrl,
    DateTimeOffset ExpiresAt);
