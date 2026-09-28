namespace FileService.Api.Application;

// The authenticated client for the current request, with its effective settings.
public interface ICallerClient
{
    string Id { get; }

    string KeyPrefix { get; }

    string BucketName { get; }

    long MaxFileBytes { get; }

    IReadOnlySet<string> AllowedContentTypes { get; }
}
