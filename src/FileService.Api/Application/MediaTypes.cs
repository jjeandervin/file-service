using System.Net.Http.Headers;

namespace FileService.Api.Application;

public static class MediaTypes
{
    public static string? TryNormalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 100 ||
            !MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            parsed.MediaType is null || parsed.Parameters.Count > 0 || parsed.MediaType.Contains('*'))
        {
            return null;
        }

        return parsed.MediaType.ToLowerInvariant();
    }
}
