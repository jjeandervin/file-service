using FileService.Api.Application;
using FileService.Api.Configuration;
using FileService.Api.Domain;
using FileService.Api.Infrastructure.Data;
using FileService.Api.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FileService.Api.Tests;

public sealed class PendingUploadCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Removes_only_pending_uploads_older_than_the_ttl()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var seed = TestClients.CreateInMemoryDb(dbName))
        {
            seed.Files.AddRange(
                TestClients.PendingRecord("hooviepack", Now.AddHours(-25)),
                TestClients.PendingRecord("acbf", Now.AddHours(-30), bucketName: "acbf-bucket"),
                TestClients.PendingRecord("hooviepack", Now.AddHours(-23)),
                TestClients.PendingRecord("hooviepack", Now.AddDays(-10), status: FileStatus.Ready));
            await seed.SaveChangesAsync();
        }

        var storage = new FakeObjectStorage();
        await using var db = TestClients.CreateInMemoryDb(dbName);
        var removed = await CreateCleaner(db, storage, new FakeTimeProvider(Now)).RunAsync();

        Assert.Equal(2, removed);
        Assert.Equal(
            ["acbf-bucket", TestClients.GlobalBucket],
            storage.DeletedObjects.Select(x => x.Bucket));
        var remaining = await db.Files.OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal([FileStatus.Ready, FileStatus.Pending], remaining.Select(x => x.Status));
    }

    [Fact]
    public async Task Works_through_more_than_one_batch()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var seed = TestClients.CreateInMemoryDb(dbName))
        {
            seed.Files.AddRange(Enumerable.Range(0, 250)
                .Select(i => TestClients.PendingRecord("hooviepack", Now.AddHours(-48).AddSeconds(i))));
            await seed.SaveChangesAsync();
        }

        var storage = new FakeObjectStorage();
        await using var db = TestClients.CreateInMemoryDb(dbName);
        var removed = await CreateCleaner(db, storage, new FakeTimeProvider(Now)).RunAsync();

        Assert.Equal(250, removed);
        Assert.Equal(250, storage.DeletedKeys.Distinct().Count());
        Assert.Empty(await db.Files.ToListAsync());
    }

    [Fact]
    public async Task A_failed_object_delete_keeps_the_row_and_is_retried_next_cycle()
    {
        var dbName = Guid.NewGuid().ToString();
        var failing = TestClients.PendingRecord("hooviepack", Now.AddHours(-26));
        await using (var seed = TestClients.CreateInMemoryDb(dbName))
        {
            seed.Files.AddRange(failing, TestClients.PendingRecord("hooviepack", Now.AddHours(-25)));
            await seed.SaveChangesAsync();
        }

        var storage = new FakeObjectStorage { FailDeleteFor = key => key == failing.StorageKey };
        await using (var db = TestClients.CreateInMemoryDb(dbName))
        {
            Assert.Equal(1, await CreateCleaner(db, storage, new FakeTimeProvider(Now)).RunAsync());
        }

        // S3 first, then the row: the object delete failed, so the row is still there to retry.
        await using (var db = TestClients.CreateInMemoryDb(dbName))
        {
            Assert.Equal(failing.Id, (await db.Files.SingleAsync()).Id);
        }

        storage.FailDeleteFor = _ => false;
        await using (var db = TestClients.CreateInMemoryDb(dbName))
        {
            Assert.Equal(1, await CreateCleaner(db, storage, new FakeTimeProvider(Now)).RunAsync());
            Assert.Empty(await db.Files.ToListAsync());
        }
    }

    [Fact]
    public async Task Service_runs_at_start_and_then_every_interval()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var storage = new FakeObjectStorage();
        await using var services = CreateServices(storage, timeProvider, options => options.PendingCleanupIntervalMinutes = 30);
        await SeedAsync(services, TestClients.PendingRecord("hooviepack", Now.AddHours(-25)));
        var service = services.GetServices<IHostedService>()
            .OfType<PendingUploadCleanupService>().Single();

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => storage.DeletedKeys.Count == 1);

        // Crosses the 24-hour TTL an hour from now, so only the third 30-minute tick removes it.
        await SeedAsync(services, TestClients.PendingRecord("hooviepack", Now.AddHours(-23)));
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(100);
        Assert.Single(storage.DeletedKeys);
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(100);
        Assert.Single(storage.DeletedKeys);
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await WaitUntilAsync(() => storage.DeletedKeys.Count == 2);

        await service.StopAsync(CancellationToken.None);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Service_survives_failing_cycles()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var storage = new FakeObjectStorage();
        await using var services = CreateServices(
            storage,
            timeProvider,
            options => options.PendingCleanupIntervalMinutes = 60,
            // Every cycle throws while resolving the database.
            breakDatabase: true);
        var service = services.GetServices<IHostedService>()
            .OfType<PendingUploadCleanupService>().Single();

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        timeProvider.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(100);

        Assert.False(service.ExecuteTask!.IsCompleted);
        await service.StopAsync(CancellationToken.None);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Service_does_nothing_when_disabled()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var storage = new FakeObjectStorage();
        await using var services = CreateServices(storage, timeProvider, options => options.PendingCleanupEnabled = false);
        await SeedAsync(services, TestClients.PendingRecord("hooviepack", Now.AddHours(-25)));
        var service = services.GetServices<IHostedService>()
            .OfType<PendingUploadCleanupService>().Single();

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        timeProvider.Advance(TimeSpan.FromDays(1));

        Assert.Empty(storage.DeletedKeys);
    }

    private static PendingUploadCleaner CreateCleaner(
        FilesDbContext db,
        IObjectStorage storage,
        TimeProvider timeProvider) => new(
        db,
        storage,
        Options.Create(TestClients.StorageOptions()),
        timeProvider,
        NullLogger<PendingUploadCleaner>.Instance);

    private static ServiceProvider CreateServices(
        FakeObjectStorage storage,
        FakeTimeProvider timeProvider,
        Action<FileStorageOptions> configure,
        bool breakDatabase = false)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton<IObjectStorage>(storage);
        services.AddSingleton(Options.Create(TestClients.StorageOptions(configure)));
        if (breakDatabase)
        {
            services.AddScoped<FilesDbContext>(_ => throw new InvalidOperationException("Simulated database outage."));
        }
        else
        {
            services.AddDbContext<FilesDbContext>(options => options.UseInMemoryDatabase(dbName));
        }

        services.AddScoped<PendingUploadCleaner>();
        services.AddHostedService<PendingUploadCleanupService>();
        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(IServiceProvider services, FileRecord record)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        db.Files.Add(record);
        await db.SaveChangesAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the cleanup service.");
            await Task.Delay(10);
        }
    }
}
