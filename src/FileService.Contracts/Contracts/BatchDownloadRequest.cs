using System.ComponentModel.DataAnnotations;

namespace FileService.Contracts;

public sealed class BatchDownloadRequest
{
    public const int MaxFileIds = 100;

    public BatchDownloadRequest()
    {
    }

    public BatchDownloadRequest(IEnumerable<Guid> fileIds) => FileIds = [.. fileIds];

    // 1–100 distinct IDs. Checked by the service, which returns 400 otherwise.
    [Required]
    public List<Guid> FileIds { get; set; } = [];
}
