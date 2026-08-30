namespace HooviePack.Files.Domain;

public sealed record UploadResponse(
    Guid FileId,
    string UploadUrl,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    string UploadToken);
