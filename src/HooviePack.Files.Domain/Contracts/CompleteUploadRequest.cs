using System.ComponentModel.DataAnnotations;

namespace HooviePack.Files.Domain;

public sealed class CompleteUploadRequest
{
    public CompleteUploadRequest()
    {
    }

    public CompleteUploadRequest(string uploadToken) => UploadToken = uploadToken;

    [Required, StringLength(256, MinimumLength = 32)]
    public string UploadToken { get; set; } = string.Empty;
}
