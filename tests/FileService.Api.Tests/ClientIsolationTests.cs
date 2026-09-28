using FileService.Api.Application;
using FileService.Api.Domain;
using FileService.Api.Infrastructure.Security;
using FileService.Api.Infrastructure.Storage;
using FileService.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FileService.Api.Tests;

public sealed class ClientIsolationTests
{
    [Fact]
    public async Task Keys_use_each_clients_prefix_and_records_capture_owner_and_bucket()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage();
        var acbf = TestClients.Create("acbf", "acbf", bucketName: "acbf-bucket");

        var hooviePackUpload = await TestClients.CreateManager(db, storage, TestClients.HooviePack)
            .CreateUploadAsync(ValidRequest());
        Assert.Equal(TestClients.GlobalBucket, storage.UploadBucket);
        var acbfUpload = await TestClients.CreateManager(db, storage, acbf).CreateUploadAsync(ValidRequest());
        Assert.Equal("acbf-bucket", storage.UploadBucket);

        var hooviePackRecord = await db.Files.SingleAsync(x => x.Id == hooviePackUpload.FileId);
        Assert.Equal("hooviepack", hooviePackRecord.ClientId);
        Assert.Equal($"files/{hooviePackUpload.FileId:N}/original", hooviePackRecord.StorageKey);
        Assert.Equal(TestClients.GlobalBucket, hooviePackRecord.BucketName);
        var acbfRecord = await db.Files.SingleAsync(x => x.Id == acbfUpload.FileId);
        Assert.Equal("acbf", acbfRecord.ClientId);
        Assert.Equal($"acbf/{acbfUpload.FileId:N}/original", acbfRecord.StorageKey);
        Assert.Equal("acbf-bucket", acbfRecord.BucketName);
    }

    [Fact]
    public async Task A_client_cannot_complete_download_or_delete_another_clients_file()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(1234, "image/jpeg") };
        var owner = TestClients.CreateManager(db, storage, TestClients.HooviePack);
        var other = TestClients.CreateManager(db, storage, TestClients.Acbf);
        var pending = await owner.CreateUploadAsync(ValidRequest());
        var ready = await owner.CreateUploadAsync(ValidRequest());
        await owner.CompleteUploadAsync(ready.FileId, new CompleteUploadRequest(ready.UploadToken));

        // The right upload token still isn't enough: ownership is checked first.
        var complete = await Assert.ThrowsAsync<FileApiException>(() =>
            other.CompleteUploadAsync(pending.FileId, new CompleteUploadRequest(pending.UploadToken)));
        var download = await Assert.ThrowsAsync<FileApiException>(() => other.CreateDownloadAsync(ready.FileId));
        var delete = await Assert.ThrowsAsync<FileApiException>(() => other.DeleteAsync(ready.FileId));
        var missing = await Assert.ThrowsAsync<FileApiException>(() => other.DeleteAsync(Guid.CreateVersion7()));

        // Identical to a file that does not exist.
        foreach (var exception in new[] { complete, download, delete })
        {
            Assert.Equal(404, exception.StatusCode);
            Assert.Equal(missing.Title, exception.Title);
            Assert.Equal(missing.Detail, exception.Detail);
        }

        Assert.Empty(storage.DeletedKeys);
        Assert.Equal(2, await db.Files.CountAsync());
        Assert.Equal(FileStatus.Pending, (await db.Files.SingleAsync(x => x.Id == pending.FileId)).Status);

        // The owner is unaffected.
        await owner.CreateDownloadAsync(ready.FileId);
        await owner.DeleteAsync(ready.FileId);
        Assert.Single(storage.DeletedKeys);
    }

    [Fact]
    public async Task Limits_and_content_types_come_from_the_callers_effective_settings()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage();
        var small = TestClients.Create("acbf", "acbf", maxFileBytes: 100, allowedContentTypes: ["image/png", "application/pdf"]);
        var manager = TestClients.CreateManager(db, storage, small);

        var tooLarge = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CreateUploadAsync(new CreateUploadRequest("a.png", "image/png", 101)));
        var wrongType = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CreateUploadAsync(new CreateUploadRequest("a.jpg", "image/jpeg", 50)));
        await manager.CreateUploadAsync(new CreateUploadRequest("a.png", "image/png", 100));
        await manager.CreateUploadAsync(new CreateUploadRequest("a.pdf", "application/pdf", 100));

        Assert.Equal(400, tooLarge.StatusCode);
        Assert.Contains("100 bytes", tooLarge.Detail, StringComparison.Ordinal);
        Assert.Equal(400, wrongType.StatusCode);
        // The hooviepack client, with global defaults, still accepts JPEGs up to 10 MiB.
        await TestClients.CreateManager(db, storage).CreateUploadAsync(new CreateUploadRequest("a.jpg", "image/jpeg", 10 * 1024 * 1024));
    }

    [Fact]
    public async Task Rows_without_a_bucket_use_the_global_bucket_and_rows_with_one_keep_it()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(1234, "image/jpeg") };
        var now = DateTimeOffset.UtcNow;
        var legacyRow = TestClients.PendingRecord("hooviepack", now, bucketName: null, FileStatus.Ready);
        var pinnedRow = TestClients.PendingRecord("hooviepack", now, bucketName: "old-bucket", FileStatus.Ready);
        db.Files.AddRange(legacyRow, pinnedRow);
        await db.SaveChangesAsync();
        // The client's bucket has since changed; existing objects must still be found where they were written.
        var manager = TestClients.CreateManager(db, storage, TestClients.Create("hooviepack", "files", bucketName: "new-bucket"));

        await manager.CreateDownloadAsync(legacyRow.Id);
        Assert.Equal(TestClients.GlobalBucket, storage.DownloadBucket);
        await manager.CreateDownloadAsync(pinnedRow.Id);
        Assert.Equal("old-bucket", storage.DownloadBucket);
        await manager.DeleteAsync(pinnedRow.Id);
        await manager.DeleteAsync(legacyRow.Id);

        Assert.Equal(
            [("old-bucket", pinnedRow.StorageKey), (TestClients.GlobalBucket, legacyRow.StorageKey)],
            storage.DeletedObjects);
    }

    [Fact]
    public async Task Legacy_mode_keeps_todays_keys_and_serves_existing_files()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(1234, "image/jpeg") };
        var registry = ClientRegistry.Create(
            [],
            "legacy-internal-api-key-with-32-characters",
            TestClients.StorageOptions());
        var legacyClient = Assert.Single(registry.Clients);
        // A row written before clients existed: ClientId came from the database default, no bucket.
        var existing = TestClients.PendingRecord(FileRecord.DefaultClientId, DateTimeOffset.UtcNow, bucketName: null, FileStatus.Ready);
        existing.StorageKey = $"files/{existing.Id:N}/original";
        db.Files.Add(existing);
        await db.SaveChangesAsync();
        var manager = TestClients.CreateManager(db, storage, legacyClient);

        var upload = await manager.CreateUploadAsync(ValidRequest());
        await manager.CompleteUploadAsync(upload.FileId, new CompleteUploadRequest(upload.UploadToken));
        var download = await manager.CreateDownloadAsync(upload.FileId);
        var existingDownload = await manager.CreateDownloadAsync(existing.Id);
        await manager.DeleteAsync(existing.Id);

        Assert.Equal($"files/{upload.FileId:N}/original", storage.UploadKey);
        Assert.Equal(upload.FileId, download.FileId);
        Assert.Equal(existing.Id, existingDownload.FileId);
        Assert.Equal(TestClients.GlobalBucket, storage.DownloadBucket);
        Assert.Equal((TestClients.GlobalBucket, existing.StorageKey), Assert.Single(storage.DeletedObjects));
        Assert.Equal("hooviepack", (await db.Files.SingleAsync()).ClientId);
    }

    private static CreateUploadRequest ValidRequest() => new("poster.jpg", "image/jpeg", 1234);
}
