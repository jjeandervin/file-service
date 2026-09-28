using System.Security.Cryptography;
using System.Text;
using FileService.Api.Configuration;
using FileService.Api.Domain;
using FileService.Api.Infrastructure.Data;
using FileService.Api.Infrastructure.Storage;
using FileService.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileService.Api.Application;

public interface IFileManager
{
    Task<UploadResponse> CreateUploadAsync(
        CreateUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<FileMetadataResponse> CompleteUploadAsync(
        Guid fileId,
        CompleteUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<DownloadResponse> CreateDownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    Task<BatchDownloadResponse> CreateDownloadsAsync(
        BatchDownloadRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default);
}

public sealed class FileManager(
    FilesDbContext db,
    IObjectStorage objectStorage,
    ICallerClient caller,
    IOptions<FileStorageOptions> options,
    TimeProvider timeProvider,
    ILogger<FileManager> logger) : IFileManager
{
    private const string ObjectMismatchDetail = "The uploaded object does not match its declared size and content type.";

    private readonly FileStorageOptions _options = options.Value;

    public async Task<UploadResponse> CreateUploadAsync(
        CreateUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var originalFileName = NormalizeFileName(request.FileName);
        var contentType = NormalizeContentType(request.ContentType);
        if (!caller.AllowedContentTypes.Contains(contentType))
        {
            throw FileApiException.BadRequest("The file content type is not supported.");
        }

        if (request.Size <= 0 || request.Size > caller.MaxFileBytes)
        {
            throw FileApiException.BadRequest(
                $"Files must be non-empty and no larger than {caller.MaxFileBytes} bytes.");
        }

        var now = timeProvider.GetUtcNow();
        var fileId = Guid.CreateVersion7(now);
        var uploadToken = CreateUploadToken();
        var record = new FileRecord
        {
            Id = fileId,
            ClientId = caller.Id,
            BucketName = caller.BucketName,
            StorageKey = BuildStorageKey(caller.KeyPrefix, fileId),
            OriginalFileName = originalFileName,
            ContentType = contentType,
            DeclaredSize = request.Size,
            UploadTokenHash = HashUploadToken(uploadToken),
            CreatedAt = now
        };
        db.Files.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        var expiresAt = now.AddMinutes(_options.UploadUrlLifetimeMinutes);
        try
        {
            var presigned = await objectStorage.CreateUploadRequestAsync(
                BucketOf(record),
                record.StorageKey,
                contentType,
                request.Size,
                expiresAt,
                cancellationToken);
            return new UploadResponse(
                record.Id,
                presigned.Url,
                expiresAt,
                presigned.RequiredHeaders,
                uploadToken);
        }
        catch
        {
            db.Files.Remove(record);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "Could not remove metadata for failed upload initialization {FileId}.",
                    record.Id);
            }

            throw;
        }
    }

    public async Task<FileMetadataResponse> CompleteUploadAsync(
        Guid fileId,
        CompleteUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var record = await FindOwnedAsync(fileId, cancellationToken)
            ?? throw FileApiException.NotFound();
        if (!UploadTokenMatches(request.UploadToken, record.UploadTokenHash))
        {
            // Do not reveal whether a guessed FileId exists.
            throw FileApiException.NotFound();
        }

        if (record.Status == FileStatus.Ready)
        {
            return MapMetadata(record);
        }

        var bucketName = BucketOf(record);
        var metadata = await objectStorage.GetMetadataAsync(bucketName, record.StorageKey, cancellationToken);
        if (metadata is null)
        {
            throw FileApiException.Conflict("The object has not been uploaded or is no longer available.");
        }

        var actualContentType = MediaTypes.TryNormalize(metadata.ContentType);
        if (metadata.Size != record.DeclaredSize ||
            !string.Equals(actualContentType, record.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw await RejectUploadAsync(record, cancellationToken);
        }

        if (_options.VerifyContentSignature && ContentSignatures.IsKnown(record.ContentType))
        {
            var prefix = await objectStorage.ReadPrefixAsync(
                bucketName,
                record.StorageKey,
                ContentSignatures.PrefixLength,
                cancellationToken)
                ?? throw FileApiException.Conflict("The object has not been uploaded or is no longer available.");
            if (!ContentSignatures.Matches(record.ContentType, prefix))
            {
                logger.LogWarning(
                    "Upload {FileId} for client {ClientId} does not start with a {ContentType} signature.",
                    record.Id,
                    record.ClientId,
                    record.ContentType);
                throw await RejectUploadAsync(record, cancellationToken);
            }
        }

        record.ActualSize = metadata.Size;
        record.Status = FileStatus.Ready;
        record.UploadedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return MapMetadata(record);
    }

    public async Task<DownloadResponse> CreateDownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        var record = await db.Files.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == fileId && x.ClientId == caller.Id && x.Status == FileStatus.Ready,
                cancellationToken)
            ?? throw FileApiException.NotFound();
        var bucketName = BucketOf(record);
        var metadata = await objectStorage.GetMetadataAsync(bucketName, record.StorageKey, cancellationToken);
        if (metadata is null)
        {
            logger.LogWarning("S3 object is missing for ready file {FileId}.", record.Id);
            throw FileApiException.NotFound();
        }

        if (metadata.Size != record.ActualSize ||
            !string.Equals(MediaTypes.TryNormalize(metadata.ContentType), record.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("S3 object metadata no longer matches file {FileId}.", record.Id);
            throw FileApiException.NotFound();
        }

        var expiresAt = timeProvider.GetUtcNow().AddMinutes(_options.DownloadUrlLifetimeMinutes);
        var url = await objectStorage.CreateDownloadUrlAsync(
            bucketName,
            record.StorageKey,
            record.OriginalFileName,
            record.ContentType,
            expiresAt,
            cancellationToken);
        return new DownloadResponse(record.Id, url, expiresAt);
    }

    // Unlike CreateDownloadAsync, this trusts the Ready status and skips the per-object S3 HEAD.
    public async Task<BatchDownloadResponse> CreateDownloadsAsync(
        BatchDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var fileIds = request.FileIds ?? [];
        if (fileIds.Count is < 1 or > BatchDownloadRequest.MaxFileIds)
        {
            throw FileApiException.BadRequest(
                $"Between 1 and {BatchDownloadRequest.MaxFileIds} file IDs are required.");
        }

        if (fileIds.Distinct().Count() != fileIds.Count)
        {
            throw FileApiException.BadRequest("File IDs must be distinct.");
        }

        var records = await db.Files.AsNoTracking()
            .Where(x => fileIds.Contains(x.Id) && x.ClientId == caller.Id && x.Status == FileStatus.Ready)
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var expiresAt = timeProvider.GetUtcNow().AddMinutes(_options.DownloadUrlLifetimeMinutes);
        var items = new List<DownloadResponse>(records.Count);
        var missing = new List<Guid>();
        foreach (var fileId in fileIds)
        {
            if (!records.TryGetValue(fileId, out var record))
            {
                missing.Add(fileId);
                continue;
            }

            var url = await objectStorage.CreateDownloadUrlAsync(
                BucketOf(record),
                record.StorageKey,
                record.OriginalFileName,
                record.ContentType,
                expiresAt,
                cancellationToken);
            items.Add(new DownloadResponse(record.Id, url, expiresAt));
        }

        return new BatchDownloadResponse(items, missing);
    }

    public async Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var record = await FindOwnedAsync(fileId, cancellationToken)
            ?? throw FileApiException.NotFound();

        // S3 deletion is idempotent. Deleting the object first makes a metadata-save retry safe.
        await objectStorage.DeleteAsync(BucketOf(record), record.StorageKey, cancellationToken);
        db.Files.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string BuildStorageKey(string prefix, Guid fileId) =>
        $"{prefix.Trim().TrimEnd('/')}/{fileId:N}/original";

    // Another client's file is indistinguishable from one that does not exist.
    private Task<FileRecord?> FindOwnedAsync(Guid fileId, CancellationToken cancellationToken) =>
        db.Files.SingleOrDefaultAsync(x => x.Id == fileId && x.ClientId == caller.Id, cancellationToken);

    private string BucketOf(FileRecord record) => record.BucketName ?? _options.BucketName;

    // Removes an upload that failed verification, returning the 400 for the caller to throw.
    private async Task<FileApiException> RejectUploadAsync(FileRecord record, CancellationToken cancellationToken)
    {
        await objectStorage.DeleteAsync(BucketOf(record), record.StorageKey, cancellationToken);
        db.Files.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
        return FileApiException.BadRequest(ObjectMismatchDetail);
    }

    private static FileMetadataResponse MapMetadata(FileRecord record) => new(
        record.Id,
        record.OriginalFileName,
        record.ContentType,
        record.ActualSize ?? record.DeclaredSize,
        record.CreatedAt);

    private static string NormalizeFileName(string fileName)
    {
        var normalizedSeparators = (fileName ?? string.Empty).Replace('\\', '/');
        var safeName = normalizedSeparators.Split('/').LastOrDefault()?.Trim() ?? string.Empty;
        if (safeName.Length is < 1 or > 255 || safeName is "." or ".." || safeName.Any(char.IsControl))
        {
            throw FileApiException.BadRequest("The file name is invalid.");
        }

        return safeName;
    }

    private static string NormalizeContentType(string contentType) =>
        MediaTypes.TryNormalize(contentType)
        ?? throw FileApiException.BadRequest("The content type is invalid.");

    private static string CreateUploadToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] HashUploadToken(string uploadToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(uploadToken ?? string.Empty));

    private static bool UploadTokenMatches(string uploadToken, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(HashUploadToken(uploadToken), expectedHash);
}
