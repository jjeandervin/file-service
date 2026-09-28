namespace FileService.Contracts;

// Missing lists every requested ID without a URL: not found, not Ready, or owned by another client.
public sealed record BatchDownloadResponse(
    IReadOnlyList<DownloadResponse> Items,
    IReadOnlyList<Guid> Missing);
