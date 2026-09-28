using FileService.Api.Application;
using FileService.Api.Configuration;
using FileService.Api.Infrastructure.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FileService.Api.Infrastructure.Storage;

// HEADs the global bucket and every client bucket. Registered only when FileStorage:CheckBucketOnReady is true.
public sealed class S3BucketHealthCheck(
    IObjectStorage objectStorage,
    ClientRegistry clients,
    IOptions<FileStorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var buckets = clients.Clients
            .Select(x => x.BucketName)
            .Prepend(options.Value.BucketName)
            .Distinct(StringComparer.Ordinal);
        try
        {
            foreach (var bucket in buckets)
            {
                if (!await objectStorage.BucketExistsAsync(bucket, cancellationToken))
                {
                    return HealthCheckResult.Unhealthy($"S3 bucket '{bucket}' does not exist.");
                }
            }

            return HealthCheckResult.Healthy("S3 buckets are reachable.");
        }
        catch (Exception exception) when (exception is ObjectStorageUnavailableException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("S3 is unavailable.", exception);
        }
    }
}
