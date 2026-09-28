using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using FileService.Api.Application;
using FileService.Api.Configuration;
using Microsoft.Extensions.Options;

namespace FileService.Api.Infrastructure.Storage;

public sealed record PresignedObjectRequest(
    string Url,
    IReadOnlyDictionary<string, string> RequiredHeaders);

public sealed record ObjectMetadata(long Size, string ContentType);

public interface IObjectStorage
{
    Task<PresignedObjectRequest> CreateUploadRequestAsync(
        string bucketName,
        string storageKey,
        string contentType,
        long size,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<string> CreateDownloadUrlAsync(
        string bucketName,
        string storageKey,
        string originalFileName,
        string contentType,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<ObjectMetadata?> GetMetadataAsync(
        string bucketName,
        string storageKey,
        CancellationToken cancellationToken = default);

    // Returns up to the first count bytes of the object, or null when it does not exist.
    Task<byte[]?> ReadPrefixAsync(
        string bucketName,
        string storageKey,
        int count,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string bucketName, string storageKey, CancellationToken cancellationToken = default);

    Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default);
}

public sealed class S3ObjectStorage(
    IAmazonS3 s3,
    IOptions<FileStorageOptions> options) : IObjectStorage
{
    private readonly FileStorageOptions _options = options.Value;

    public async Task<PresignedObjectRequest> CreateUploadRequestAsync(
        string bucketName,
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
                BucketName = bucketName,
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
        string bucketName,
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
                BucketName = bucketName,
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
        string bucketName,
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucketName, Key = storageKey },
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

    public async Task<byte[]?> ReadPrefixAsync(
        string bucketName,
        string storageKey,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        try
        {
            using var response = await s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = bucketName,
                    Key = storageKey,
                    ByteRange = new ByteRange(0, count - 1)
                },
                cancellationToken);
            var buffer = new byte[count];
            var read = await response.ResponseStream.ReadAtLeastAsync(
                buffer,
                count,
                throwOnEndOfStream: false,
                cancellationToken);
            return buffer[..read];
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // A zero-byte object has no satisfiable range.
            return [];
        }
        catch (Exception exception) when (exception is AmazonS3Exception or Amazon.Runtime.AmazonServiceException)
        {
            throw new ObjectStorageUnavailableException("Could not read an object.", exception);
        }
    }

    public async Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucketName }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception exception) when (exception is AmazonS3Exception or Amazon.Runtime.AmazonServiceException)
        {
            throw new ObjectStorageUnavailableException("Could not inspect a bucket.", exception);
        }
    }

    public async Task DeleteAsync(string bucketName, string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucketName, Key = storageKey },
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
