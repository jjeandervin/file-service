using System.Text;
using FileService.Api.Application;
using FileService.Api.Domain;
using FileService.Api.Infrastructure.Storage;
using FileService.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FileService.Api.Tests;

public sealed class ContentSignatureTests
{
    [Theory]
    [InlineData("tiny.jpg", "image/jpeg")]
    [InlineData("tiny.png", "image/png")]
    [InlineData("tiny.webp", "image/webp")]
    public async Task Real_images_pass_verification(string fileName, string contentType)
    {
        var content = TestFiles.Read(fileName);
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage { Content = content, Metadata = new ObjectMetadata(content.Length, contentType) };
        var manager = TestClients.CreateManager(db, storage);
        var upload = await manager.CreateUploadAsync(new CreateUploadRequest(fileName, contentType, content.Length));

        var completed = await manager.CompleteUploadAsync(upload.FileId, new CompleteUploadRequest(upload.UploadToken));

        Assert.Equal(content.Length, completed.Size);
        Assert.Equal(1, storage.PrefixReads);
        Assert.Equal(FileStatus.Ready, (await db.Files.SingleAsync()).Status);
    }

    [Fact]
    public async Task Text_declared_as_jpeg_is_deleted_and_rejected_like_a_size_mismatch()
    {
        var content = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        var (exception, storage, db) = await CompleteAsync(content, "image/jpeg");

        await using (db)
        {
            Assert.Equal(400, exception.StatusCode);
            Assert.Equal(await SizeMismatchDetailAsync(), exception.Detail);
            Assert.Single(storage.DeletedKeys);
            Assert.Empty(await db.Files.ToListAsync());
        }
    }

    [Theory]
    [InlineData("tiny.jpg", "image/jpeg", 2)]
    [InlineData("tiny.png", "image/png", 7)]
    [InlineData("tiny.webp", "image/webp", 11)]
    public async Task Truncated_files_are_rejected(string fileName, string contentType, int length)
    {
        var content = TestFiles.Read(fileName)[..length];
        var (exception, storage, db) = await CompleteAsync(content, contentType);

        await using (db)
        {
            Assert.Equal(400, exception.StatusCode);
            Assert.Single(storage.DeletedKeys);
            Assert.Empty(await db.Files.ToListAsync());
        }
    }

    [Fact]
    public async Task A_png_declared_as_webp_is_rejected()
    {
        var (exception, _, db) = await CompleteAsync(TestFiles.Read("tiny.png"), "image/webp");

        await using (db)
        {
            Assert.Equal(400, exception.StatusCode);
        }
    }

    [Fact]
    public async Task Types_without_a_known_signature_pass_without_reading_the_object()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage { Content = "%PDF-1.7"u8.ToArray(), Metadata = new ObjectMetadata(8, "application/pdf") };
        var client = TestClients.Create("acbf", "acbf", allowedContentTypes: ["application/pdf"]);
        var manager = TestClients.CreateManager(db, storage, client);
        var upload = await manager.CreateUploadAsync(new CreateUploadRequest("doc.pdf", "application/pdf", 8));

        await manager.CompleteUploadAsync(upload.FileId, new CompleteUploadRequest(upload.UploadToken));

        Assert.Equal(0, storage.PrefixReads);
    }

    [Fact]
    public async Task Verification_can_be_turned_off()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage { Content = "not a jpeg"u8.ToArray(), Metadata = new ObjectMetadata(10, "image/jpeg") };
        var manager = TestClients.CreateManager(db, storage, configure: options => options.VerifyContentSignature = false);
        var upload = await manager.CreateUploadAsync(new CreateUploadRequest("a.jpg", "image/jpeg", 10));

        await manager.CompleteUploadAsync(upload.FileId, new CompleteUploadRequest(upload.UploadToken));

        Assert.Equal(0, storage.PrefixReads);
        Assert.Equal(FileStatus.Ready, (await db.Files.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF }, true)]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8 }, false)]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, true)]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0B }, false)]
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, true)]
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 }, false)]
    [InlineData("text/plain", new byte[0], true)]
    public void Signatures_match_the_spec(string contentType, byte[] prefix, bool expected) =>
        Assert.Equal(expected, ContentSignatures.Matches(contentType, prefix));

    private static async Task<(FileApiException Exception, FakeObjectStorage Storage, Infrastructure.Data.FilesDbContext Db)> CompleteAsync(
        byte[] content,
        string contentType)
    {
        var db = TestClients.CreateInMemoryDb();
        var storage = new FakeObjectStorage { Content = content, Metadata = new ObjectMetadata(content.Length, contentType) };
        var manager = TestClients.CreateManager(db, storage);
        var upload = await manager.CreateUploadAsync(new CreateUploadRequest("upload", contentType, content.Length));
        var exception = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CompleteUploadAsync(upload.FileId, new CompleteUploadRequest(upload.UploadToken)));
        return (exception, storage, db);
    }

    private static async Task<string> SizeMismatchDetailAsync()
    {
        await using var db = TestClients.CreateInMemoryDb();
        var manager = TestClients.CreateManager(db, new FakeObjectStorage { Metadata = new ObjectMetadata(1, "image/jpeg") });
        var upload = await manager.CreateUploadAsync(new CreateUploadRequest("a.jpg", "image/jpeg", 2));
        var exception = await Assert.ThrowsAsync<FileApiException>(() =>
            manager.CompleteUploadAsync(upload.FileId, new CompleteUploadRequest(upload.UploadToken)));
        return exception.Detail;
    }
}
