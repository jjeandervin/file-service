using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using HooviePack.Files.Api.Configuration;
using HooviePack.Files.Api.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace HooviePack.Files.Api.Tests;

public sealed class S3ObjectStorageTests
{
    [Fact]
    public async Task Upload_url_is_signed_for_one_put_key_content_type_and_length_without_real_Aws()
    {
        using var client = new AmazonS3Client(
            new BasicAWSCredentials("test-access-key", "test-secret-key"),
            RegionEndpoint.USEast1);
        var storage = CreateStorage(client);

        var result = await storage.CreateUploadRequestAsync(
            "files/0198/original",
            "image/jpeg",
            4821932,
            DateTimeOffset.UtcNow.AddMinutes(5));

        var uri = new Uri(result.Url);
        var decoded = Uri.UnescapeDataString(uri.Query);
        Assert.Contains("private-test-bucket", uri.Host, StringComparison.Ordinal);
        Assert.Equal("/files/0198/original", uri.AbsolutePath);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-length", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("image/jpeg", result.RequiredHeaders["Content-Type"]);
        Assert.DoesNotContain("Authorization", result.RequiredHeaders.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_url_is_signed_for_one_get_key_with_safe_response_headers()
    {
        using var client = new AmazonS3Client(
            new BasicAWSCredentials("test-access-key", "test-secret-key"),
            RegionEndpoint.USEast1);
        var storage = CreateStorage(client);

        var url = await storage.CreateDownloadUrlAsync(
            "files/0198/original",
            "poster\"\r\n.jpg",
            "image/jpeg",
            DateTimeOffset.UtcNow.AddMinutes(5));

        var uri = new Uri(url);
        var decoded = Uri.UnescapeDataString(uri.Query);
        Assert.Equal("/files/0198/original", uri.AbsolutePath);
        Assert.Contains("response-content-type=image/jpeg", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("poster___.jpg", decoded, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", decoded, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", decoded, StringComparison.Ordinal);
    }

    private static S3ObjectStorage CreateStorage(IAmazonS3 client) => new(
        client,
        Options.Create(new FileStorageOptions
        {
            BucketName = "private-test-bucket",
            Region = "us-east-1",
            KeyPrefix = "files"
        }));
}
