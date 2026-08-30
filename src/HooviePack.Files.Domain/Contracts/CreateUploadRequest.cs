using System.ComponentModel.DataAnnotations;

namespace HooviePack.Files.Domain;

public sealed class CreateUploadRequest
{
    public CreateUploadRequest()
    {
    }

    public CreateUploadRequest(string fileName, string contentType, long size) =>
        (FileName, ContentType, Size) = (fileName, contentType, size);

    [Required, StringLength(255, MinimumLength = 1)]
    public string FileName { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 3)]
    public string ContentType { get; set; } = string.Empty;

    [Range(1, long.MaxValue)]
    public long Size { get; set; }
}
