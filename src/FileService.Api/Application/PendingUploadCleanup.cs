using FileService.Api.Configuration;
using FileService.Api.Domain;
using FileService.Api.Infrastructure.Data;
using FileService.Api.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileService.Api.Application;

// Deletes uploads that were created but never completed, once they are older than PendingUploadTtlHours.
public sealed class PendingUploadCleaner(
    FilesDbContext db,
    IObjectStorage objectStorage,
    IOptions<FileStorageOptions> options,
    TimeProvider timeProvider,
    ILogger<PendingUploadCleaner> logger)
{
    public const int BatchSize = 100;

    private readonly FileStorageOptions _options = options.Value;

    // Returns the number of records removed. A record that fails is logged, left in place, and retried next cycle.
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow().AddHours(-_options.PendingUploadTtlHours);
        var failed = new List<Guid>();
        var removed = 0;
        while (true)
        {
            var batch = await db.Files
                .Where(x => x.Status == FileStatus.Pending && x.CreatedAt < cutoff && !failed.Contains(x.Id))
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var record in batch)
            {
                try
                {
                    // Same order as FileManager.DeleteAsync: the S3 delete is idempotent, so a failed row delete is safe to retry.
                    await objectStorage.DeleteAsync(record.BucketName ?? _options.BucketName, record.StorageKey, cancellationToken);
                    db.Files.Remove(record);
                    await db.SaveChangesAsync(cancellationToken);
                    removed++;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        exception,
                        "Could not remove abandoned upload {FileId} for client {ClientId}; will retry next cycle.",
                        record.Id,
                        record.ClientId);
                    db.Entry(record).State = EntityState.Detached;
                    failed.Add(record.Id);
                }
            }

            db.ChangeTracker.Clear();
            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        if (removed > 0 || failed.Count > 0)
        {
            logger.LogInformation(
                "Abandoned-upload cleanup removed {Removed} pending uploads; {Failed} failed.",
                removed,
                failed.Count);
        }

        return removed;
    }
}

public sealed class PendingUploadCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<FileStorageOptions> options,
    TimeProvider timeProvider,
    ILogger<PendingUploadCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var storageOptions = options.Value;
        if (!storageOptions.PendingCleanupEnabled)
        {
            logger.LogInformation("Abandoned-upload cleanup is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(storageOptions.PendingCleanupIntervalMinutes),
            timeProvider);
        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Never lets a failure escape: an unhandled BackgroundService exception would stop the host.
    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PendingUploadCleaner>().RunAsync(stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Abandoned-upload cleanup failed; will retry next cycle.");
        }
    }
}
