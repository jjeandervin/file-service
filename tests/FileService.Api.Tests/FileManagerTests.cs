using FileService.Api.Application;
using FileService.Api.Domain;
using FileService.Api.Infrastructure.Data;
using FileService.Api.Infrastructure.Storage;
using FileService.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FileService.Api.Tests;

public sealed class FileManagerTests
{
    [Fact]
    public async Task CreateUpload_generates_stable_file_id_private_key_and_scoped_put()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var manager = CreateManager(db, storage, now);

        var result = await manager.CreateUploadAsync(new CreateUploadRequest
        {
            FileName = "../poster.jpg",
            ContentType = "image/jpeg",
            Size = 1234
        });

        Assert.Equal(7, result.FileId.Version);
        Assert.Equal("https://s3.example/upload", result.UploadUrl);
        Assert.Equal("image/jpeg", result.RequiredHeaders["Content-Type"]);
        Assert.NotEmpty(result.UploadToken);
        var record = await db.Files.SingleAsync();
        Assert.Equal(result.FileId, record.Id);
        Assert.Equal($"files/{result.FileId:N}/original", record.StorageKey);
        Assert.Equal("poster.jpg", record.OriginalFileName);
        Assert.Equal(FileStatus.Pending, record.Status);
        Assert.Equal(record.StorageKey, storage.UploadKey);
        Assert.Equal(1234, storage.UploadSize);
    }

    [Fact]
    public async Task CompleteUpload_verifies_object_and_returns_metadata_idempotently()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(1234, "image/jpeg") };
        var manager = CreateManager(db, storage);
        var upload = await manager.CreateUploadAsync(ValidRequest());

        var completed = await manager.CompleteUploadAsync(
            upload.FileId,
            new CompleteUploadRequest { UploadToken = upload.UploadToken });
        var repeated = await manager.CompleteUploadAsync(
            upload.FileId,
            new CompleteUploadRequest { UploadToken = upload.UploadToken });

        Assert.Equal(upload.FileId, completed.FileId);
        Assert.Equal(1234, completed.Size);
        Assert.Equal(completed, repeated);
        Assert.Equal(1, storage.MetadataRequests);
        Assert.Equal(FileStatus.Ready, (await db.Files.SingleAsync()).Status);
    }

    [Fact]
    public async Task CompleteUpload_rejects_missing_object_and_invalid_token()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var manager = CreateManager(db, storage);
        var upload = await manager.CreateUploadAsync(ValidRequest());

        var invalidToken = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CompleteUploadAsync(
                upload.FileId,
                new CompleteUploadRequest { UploadToken = new string('x', 43) }));
        Assert.Equal(404, invalidToken.StatusCode);

        var missing = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CompleteUploadAsync(
                upload.FileId,
                new CompleteUploadRequest { UploadToken = upload.UploadToken }));
        Assert.Equal(409, missing.StatusCode);
    }

    [Fact]
    public async Task CompleteUpload_removes_mismatched_object_and_metadata()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(9999, "image/jpeg") };
        var manager = CreateManager(db, storage);
        var upload = await manager.CreateUploadAsync(ValidRequest());

        var exception = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CompleteUploadAsync(
                upload.FileId,
                new CompleteUploadRequest { UploadToken = upload.UploadToken }));

        Assert.Equal(400, exception.StatusCode);
        Assert.Single(storage.DeletedKeys);
        Assert.Empty(await db.Files.ToListAsync());
    }

    [Fact]
    public async Task Download_verifies_ready_object_and_generates_short_lived_get_url()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(1234, "image/jpeg") };
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var manager = CreateManager(db, storage, now);
        var upload = await manager.CreateUploadAsync(ValidRequest());
        await manager.CompleteUploadAsync(
            upload.FileId,
            new CompleteUploadRequest { UploadToken = upload.UploadToken });

        var download = await manager.CreateDownloadAsync(upload.FileId);

        Assert.Equal(upload.FileId, download.FileId);
        Assert.Equal("https://s3.example/download", download.DownloadUrl);
        Assert.Equal(now.AddMinutes(5), download.ExpiresAt);
        Assert.Equal($"files/{upload.FileId:N}/original", storage.DownloadKey);
    }

    [Fact]
    public async Task Download_and_delete_handle_missing_files_and_delete_by_private_key()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(1234, "image/jpeg") };
        var manager = CreateManager(db, storage);

        var missingDownload = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CreateDownloadAsync(Guid.CreateVersion7()));
        Assert.Equal(404, missingDownload.StatusCode);

        var upload = await manager.CreateUploadAsync(ValidRequest());
        await manager.DeleteAsync(upload.FileId);

        Assert.Equal($"files/{upload.FileId:N}/original", Assert.Single(storage.DeletedKeys));
        Assert.Empty(await db.Files.ToListAsync());

        var missingDelete = await Assert.ThrowsAsync<FileApiException>(() => manager.DeleteAsync(upload.FileId));
        Assert.Equal(404, missingDelete.StatusCode);
    }

    [Theory]
    [InlineData("", "image/jpeg", 1)]
    [InlineData("photo.jpg", "text/html", 1)]
    [InlineData("photo.jpg", "image/jpeg; charset=utf-8", 1)]
    [InlineData("photo.jpg", "image/jpeg", 0)]
    [InlineData("photo.jpg", "image/jpeg", 10485761)]
    public async Task CreateUpload_rejects_invalid_metadata(string fileName, string contentType, long size)
    {
        await using var db = CreateDb();
        var manager = CreateManager(db, new FakeObjectStorage());

        await Assert.ThrowsAsync<FileApiException>(() => manager.CreateUploadAsync(new CreateUploadRequest
        {
            FileName = fileName,
            ContentType = contentType,
            Size = size
        }));

        Assert.Empty(await db.Files.ToListAsync());
    }

    private static CreateUploadRequest ValidRequest() => new()
    {
        FileName = "poster.jpg",
        ContentType = "image/jpeg",
        Size = 1234
    };

    private static FilesDbContext CreateDb() => TestClients.CreateInMemoryDb();

    private static FileManager CreateManager(
        FilesDbContext db,
        FakeObjectStorage storage,
        DateTimeOffset? now = null) => TestClients.CreateManager(db, storage, now: now);
}
