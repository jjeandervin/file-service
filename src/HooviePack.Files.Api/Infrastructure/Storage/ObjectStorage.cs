using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using HooviePack.Files.Api.Application;
using HooviePack.Files.Api.Configuration;
using Microsoft.Extensions.Options;

namespace HooviePack.Files.Api.Infrastructure.Storage;

public sealed record PresignedObjectRequest(
    string Url,
    IReadOnlyDictionary<string, string> RequiredHeaders);

public sealed record ObjectMetadata(long Size, string ContentType);

public interface IObjectStorage
{
    Task<PresignedObjectRequest> CreateUploadRequestAsync(
        string storageKey,
        string contentType,
        long size,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<string> CreateDownloadUrlAsync(
        string storageKey,
        string originalFileName,
        string contentType,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<ObjectMetadata?> GetMetadataAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}

public sealed class S3ObjectStorage(
    IAmazonS3 s3,
    IOptions<FileStorageOptions> options) : IObjectStorage
{
    private readonly FileStorageOptions _options = options.Value;

    public async Task<PresignedObjectRequest> CreateUploadRequestAsync(
        string storageKey,
        string contentType,
        long size,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _options.BucketName,
                Key = storageKey,
                Verb = HttpVerb.PUT,
                ContentType = contentType,
                Expires = expiresAt.UtcDateTime,
                Protocol = GetProtocol()
            };
            request.Headers.ContentLength = size;
            var url = await s3.GetPreSignedURLAsync(request);
            return new PresignedObjectRequest(
                url,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = contentType
                });
        }
        catch (Exception exception) when (exception is AmazonS3Exception or Amazon.Runtime.AmazonServiceException)
        {
            throw new ObjectStorageUnavailableException("Could not create an upload URL.", exception);
        }
    }

    public async Task<string> CreateDownloadUrlAsync(
        string storageKey,
        string originalFileName,
        string contentType,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _options.BucketName,
                Key = storageKey,
                Verb = HttpVerb.GET,
                Expires = expiresAt.UtcDateTime,
                Protocol = GetProtocol()
            };
            request.ResponseHeaderOverrides.ContentType = contentType;
            request.ResponseHeaderOverrides.ContentDisposition =
                $"inline; filename=\"{EscapeContentDispositionFileName(originalFileName)}\"";
            return await s3.GetPreSignedURLAsync(request);
        }
        catch (Exception exception) when (exception is AmazonS3Exception or Amazon.Runtime.AmazonServiceException)
        {
            throw new ObjectStorageUnavailableException("Could not create a download URL.", exception);
        }
    }

    public async Task<ObjectMetadata?> GetMetadataAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _options.BucketName, Key = storageKey },
                cancellationToken);
            return new ObjectMetadata(response.ContentLength, response.Headers.ContentType ?? string.Empty);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception exception) when (exception is AmazonS3Exception or Amazon.Runtime.AmazonServiceException)
        {
            throw new ObjectStorageUnavailableException("Could not inspect an object.", exception);
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = _options.BucketName, Key = storageKey },
                cancellationToken);
        }
        catch (Exception exception) when (exception is AmazonS3Exception or Amazon.Runtime.AmazonServiceException)
        {
            throw new ObjectStorageUnavailableException("Could not delete an object.", exception);
        }
    }

    public static IAmazonS3 CreateClient(FileStorageOptions options)
    {
        var configuration = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            configuration.ServiceURL = options.ServiceUrl;
            configuration.AuthenticationRegion = options.Region;
        }
        else
        {
            configuration.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        // The SDK's normal chain supplies environment, profile, workload, or instance-role credentials.
        return new AmazonS3Client(configuration);
    }

    private Protocol GetProtocol() =>
        Uri.TryCreate(_options.ServiceUrl, UriKind.Absolute, out var serviceUri) &&
        serviceUri.Scheme == Uri.UriSchemeHttp
            ? Protocol.HTTP
            : Protocol.HTTPS;

    private static string EscapeContentDispositionFileName(string fileName) =>
        fileName.Replace("\\", "_", StringComparison.Ordinal)
            .Replace("\"", "_", StringComparison.Ordinal)
            .Replace("\r", "_", StringComparison.Ordinal)
            .Replace("\n", "_", StringComparison.Ordinal);
}
