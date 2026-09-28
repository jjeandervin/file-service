using System.Net;
using System.Net.Http.Json;
using FileService.Api.Infrastructure.Storage;
using FileService.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FileService.Api.Tests;

// End-to-end through the real Program: configuration, authentication, controllers, EF migrations on PostgreSQL.
public sealed class HttpApiTests
{
    private const string LegacyKey = "legacy-internal-api-key-with-32-characters";
    private const string HooviePackKey = "hooviepack-api-key-with-at-least-32-characters";
    private const string AcbfKey = "acbf-api-key-with-at-least-32-characters-too";

    [PostgresFact]
    public async Task Legacy_mode_runs_a_hooviepack_style_cycle_with_todays_keys()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(1234, "image/jpeg") };
        await using var factory = new FileServiceFactory(database.ConnectionString, storage, new()
        {
            ["InternalApi:ApiKey"] = LegacyKey
        });
        using var client = factory.CreateClient();

        var unauthorized = await client.PostAsJsonAsync("/files/uploads", ValidRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", LegacyKey);
        var created = await client.PostAsJsonAsync("/files/uploads", ValidRequest());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var upload = (await created.Content.ReadFromJsonAsync<UploadResponse>())!;
        Assert.Equal($"files/{upload.FileId:N}/original", storage.UploadKey);
        Assert.Equal(TestClients.GlobalBucket, storage.UploadBucket);
        Assert.Equal("image/jpeg", upload.RequiredHeaders["Content-Type"]);

        var completed = await client.PostAsJsonAsync(
            $"/files/{upload.FileId}/complete",
            new CompleteUploadRequest(upload.UploadToken));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var download = await client.GetFromJsonAsync<DownloadResponse>($"/files/{upload.FileId}/download");
        Assert.Equal(upload.FileId, download!.FileId);
        var batch = await client.PostAsJsonAsync("/files/downloads", new BatchDownloadRequest([upload.FileId]));
        Assert.Equal(upload.FileId, Assert.Single((await batch.Content.ReadFromJsonAsync<BatchDownloadResponse>())!.Items).FileId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/files/{upload.FileId}")).StatusCode);
        Assert.Equal($"files/{upload.FileId:N}/original", Assert.Single(storage.DeletedKeys));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/files/{upload.FileId}/download")).StatusCode);
    }

    [PostgresFact]
    public async Task Another_clients_file_is_a_404_on_every_endpoint()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var storage = new FakeObjectStorage { Metadata = new ObjectMetadata(1234, "image/jpeg") };
        await using var factory = new FileServiceFactory(database.ConnectionString, storage, new()
        {
            ["Clients:0:Id"] = "hooviepack",
            ["Clients:0:ApiKey"] = HooviePackKey,
            ["Clients:0:KeyPrefix"] = "files",
            ["Clients:1:Id"] = "acbf",
            ["Clients:1:ApiKey"] = AcbfKey,
            ["Clients:1:KeyPrefix"] = "acbf"
        });
        using var hooviePack = factory.CreateClient();
        hooviePack.DefaultRequestHeaders.Add("X-Internal-Api-Key", HooviePackKey);
        using var acbf = factory.CreateClient();
        acbf.DefaultRequestHeaders.Add("X-Internal-Api-Key", AcbfKey);

        var pending = (await (await hooviePack.PostAsJsonAsync("/files/uploads", ValidRequest()))
            .Content.ReadFromJsonAsync<UploadResponse>())!;
        var ready = (await (await hooviePack.PostAsJsonAsync("/files/uploads", ValidRequest()))
            .Content.ReadFromJsonAsync<UploadResponse>())!;
        (await hooviePack.PostAsJsonAsync($"/files/{ready.FileId}/complete", new CompleteUploadRequest(ready.UploadToken)))
            .EnsureSuccessStatusCode();
        var acbfUpload = (await (await acbf.PostAsJsonAsync("/files/uploads", ValidRequest()))
            .Content.ReadFromJsonAsync<UploadResponse>())!;
        Assert.Equal($"acbf/{acbfUpload.FileId:N}/original", storage.UploadKey);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await acbf.PostAsJsonAsync($"/files/{pending.FileId}/complete", new CompleteUploadRequest(pending.UploadToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await acbf.GetAsync($"/files/{ready.FileId}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await acbf.DeleteAsync($"/files/{ready.FileId}")).StatusCode);
        var batch = await (await acbf.PostAsJsonAsync("/files/downloads", new BatchDownloadRequest([ready.FileId])))
            .Content.ReadFromJsonAsync<BatchDownloadResponse>();
        Assert.Empty(batch!.Items);
        Assert.Equal([ready.FileId], batch.Missing);

        Assert.Empty(storage.DeletedKeys);
        Assert.Equal(HttpStatusCode.OK, (await hooviePack.GetAsync($"/files/{ready.FileId}/download")).StatusCode);
    }

    [PostgresFact]
    public async Task Batch_request_validation_returns_400()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var factory = new FileServiceFactory(database.ConnectionString, new FakeObjectStorage(), new()
        {
            ["InternalApi:ApiKey"] = LegacyKey
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", LegacyKey);
        var id = Guid.CreateVersion7();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/files/downloads", new { fileIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/files/downloads", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/files/downloads", new { fileIds = new[] { id, id } })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/files/downloads", new { fileIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()) })).StatusCode);
    }

    [PostgresFact]
    public async Task Ready_checks_the_buckets_only_when_asked()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var storage = new FakeObjectStorage();
        storage.MissingBuckets.Add("acbf-bucket");
        var settings = new Dictionary<string, string?>
        {
            ["Clients:0:Id"] = "hooviepack",
            ["Clients:0:ApiKey"] = HooviePackKey,
            ["Clients:0:KeyPrefix"] = "files",
            ["Clients:1:Id"] = "acbf",
            ["Clients:1:ApiKey"] = AcbfKey,
            ["Clients:1:KeyPrefix"] = "acbf",
            ["Clients:1:BucketName"] = "acbf-bucket"
        };
        await using (var withoutCheck = new FileServiceFactory(database.ConnectionString, storage, settings))
        {
            Assert.Equal(HttpStatusCode.OK, (await withoutCheck.CreateClient().GetAsync("/health/ready")).StatusCode);
        }

        settings["FileStorage:CheckBucketOnReady"] = "true";
        await using var withCheck = new FileServiceFactory(database.ConnectionString, storage, settings);
        using var client = withCheck.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        storage.MissingBuckets.Clear();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData("InternalApi:ApiKey", LegacyKey, "not both")]
    [InlineData("Clients:1:Id", "hooviepack", "configured more than once")]
    [InlineData("Clients:1:KeyPrefix", "files/acbf", "overlapping key prefixes")]
    public void Invalid_client_configuration_stops_startup(string key, string value, string expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Clients:0:Id"] = "hooviepack",
            ["Clients:0:ApiKey"] = HooviePackKey,
            ["Clients:0:KeyPrefix"] = "files",
            ["Clients:1:Id"] = "acbf",
            ["Clients:1:ApiKey"] = AcbfKey,
            ["Clients:1:KeyPrefix"] = "acbf",
            [key] = value
        };
        // Validation runs before anything touches the database, so no PostgreSQL is needed.
        using var factory = new FileServiceFactory("Host=127.0.0.1;Port=1;Database=unused", new FakeObjectStorage(), settings);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var messages = string.Join(" | ", Flatten(exception).Select(x => x.Message));
        Assert.Contains(expected, messages, StringComparison.Ordinal);
        Assert.DoesNotContain(HooviePackKey, messages, StringComparison.Ordinal);
        Assert.DoesNotContain(AcbfKey, messages, StringComparison.Ordinal);
    }

    private static CreateUploadRequest ValidRequest() => new("poster.jpg", "image/jpeg", 1234);

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }
        }
    }

    private sealed class FileServiceFactory(
        string connectionString,
        FakeObjectStorage storage,
        Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("Database:ApplyMigrations", "true");
            builder.UseSetting("FileStorage:BucketName", TestClients.GlobalBucket);
            builder.UseSetting("FileStorage:Region", "us-east-1");
            builder.UseSetting("FileStorage:PendingCleanupEnabled", "false");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services => services.AddSingleton<IObjectStorage>(storage));
        }
    }
}
