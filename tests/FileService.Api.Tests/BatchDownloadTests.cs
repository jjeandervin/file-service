using FileService.Api.Application;
using FileService.Api.Domain;
using FileService.Api.Infrastructure.Storage;
using FileService.Contracts;

namespace FileService.Api.Tests;

public sealed class BatchDownloadTests
{
    [Fact]
    public async Task Returns_urls_only_for_the_callers_ready_files_in_request_order()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var mineReady = TestClients.PendingRecord("hooviepack", now, status: FileStatus.Ready);
        var minePending = TestClients.PendingRecord("hooviepack", now);
        var theirsReady = TestClients.PendingRecord("acbf", now, status: FileStatus.Ready);
        var mineReadyInOtherBucket = TestClients.PendingRecord("hooviepack", now, bucketName: "other-bucket", FileStatus.Ready);
        db.Files.AddRange(mineReady, minePending, theirsReady, mineReadyInOtherBucket);
        await db.SaveChangesAsync();
        var unknown = Guid.CreateVersion7();
        var manager = TestClients.CreateManager(db, storage, TestClients.HooviePack, now);

        var response = await manager.CreateDownloadsAsync(new BatchDownloadRequest(
            [theirsReady.Id, mineReadyInOtherBucket.Id, unknown, mineReady.Id, minePending.Id]));

        Assert.Equal([mineReadyInOtherBucket.Id, mineReady.Id], response.Items.Select(x => x.FileId));
        Assert.All(response.Items, item =>
        {
            Assert.Equal("https://s3.example/download", item.DownloadUrl);
            Assert.Equal(now.AddMinutes(5), item.ExpiresAt);
        });
        // "Not yours", "doesn't exist" and "not ready" look the same.
        Assert.Equal([theirsReady.Id, unknown, minePending.Id], response.Missing);
        Assert.Equal([mineReadyInOtherBucket.StorageKey, mineReady.StorageKey], storage.DownloadKeys);
        // The batch trusts Ready and skips the per-object S3 HEAD.
        Assert.Equal(0, storage.MetadataRequests);
    }

    [Fact]
    public async Task Accepts_up_to_one_hundred_ids_and_rejects_more()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var manager = TestClients.CreateManager(db, new FakeObjectStorage());

        var hundred = await manager.CreateDownloadsAsync(new BatchDownloadRequest(NewIds(100)));
        var exception = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CreateDownloadsAsync(new BatchDownloadRequest(NewIds(101))));

        Assert.Equal(100, hundred.Missing.Count);
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task Rejects_duplicate_ids()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var manager = TestClients.CreateManager(db, new FakeObjectStorage());
        var id = Guid.CreateVersion7();

        var exception = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CreateDownloadsAsync(new BatchDownloadRequest([id, Guid.CreateVersion7(), id])));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("distinct", exception.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_an_empty_list()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var manager = TestClients.CreateManager(db, new FakeObjectStorage());

        var empty = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CreateDownloadsAsync(new BatchDownloadRequest([])));
        var nullList = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CreateDownloadsAsync(new BatchDownloadRequest { FileIds = null! }));

        Assert.Equal(400, empty.StatusCode);
        Assert.Equal(400, nullList.StatusCode);
    }

    private static IEnumerable<Guid> NewIds(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid());
}
