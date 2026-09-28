using System.Text.Json;
using FileService.Api.Configuration;
using FileService.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace FileService.Api.Tests;

public sealed class ClientAuthenticationMiddlewareTests
{
    private const string ApiKey = "test-internal-api-key-with-32-characters";
    private const string AcbfApiKey = "acbf-internal-api-key-with-32-characters";

    [Fact]
    public async Task Valid_key_allows_file_request()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("/files/uploads");
        context.Request.Headers["X-Internal-Api-Key"] = ApiKey;

        await middleware.InvokeAsync(context, new CallerClientAccessor());

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Missing_or_invalid_key_rejects_file_request(string? suppliedKey)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("/files/0198fa25-483e-7000-8000-000000000001/download");
        if (suppliedKey is not null)
        {
            context.Request.Headers["X-Internal-Api-Key"] = suppliedKey;
        }

        var caller = new CallerClientAccessor();
        await middleware.InvokeAsync(context, caller);

        Assert.False(nextCalled);
        Assert.Null(caller.Client);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("Unauthorized", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "A valid internal service credential is required.",
            document.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(ApiKey, document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_request_does_not_require_key()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("/health/ready");

        await middleware.InvokeAsync(context, new CallerClientAccessor());

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(ApiKey, "hooviepack", "files")]
    [InlineData(AcbfApiKey, "acbf", "acbf")]
    public async Task Each_key_resolves_its_own_client(string suppliedKey, string expectedId, string expectedPrefix)
    {
        var middleware = new ClientAuthenticationMiddleware(
            _ => Task.CompletedTask,
            ClientRegistry.Create(
                [
                    new ClientOptions { Id = "hooviepack", ApiKey = ApiKey, KeyPrefix = "files" },
                    new ClientOptions
                    {
                        Id = "acbf",
                        ApiKey = AcbfApiKey,
                        KeyPrefix = "acbf",
                        MaxFileBytes = 1024,
                        BucketName = "acbf-bucket"
                    }
                ],
                legacyApiKey: null,
                TestClients.StorageOptions()));
        var context = CreateContext("/files/uploads");
        context.Request.Headers["X-Internal-Api-Key"] = suppliedKey;
        var caller = new CallerClientAccessor();

        await middleware.InvokeAsync(context, caller);

        Assert.NotNull(caller.Client);
        Assert.Equal(expectedId, caller.Client.Id);
        Assert.Equal(expectedPrefix, caller.Client.KeyPrefix);
        Assert.Equal(expectedId == "acbf" ? "acbf-bucket" : TestClients.GlobalBucket, caller.Client.BucketName);
        Assert.Equal(expectedId == "acbf" ? 1024 : 10 * 1024 * 1024, caller.Client.MaxFileBytes);
    }

    [Fact]
    public async Task Legacy_key_resolves_the_synthesized_hooviepack_client()
    {
        var middleware = new ClientAuthenticationMiddleware(
            _ => Task.CompletedTask,
            ClientRegistry.Create([], ApiKey, TestClients.StorageOptions()));
        var context = CreateContext("/files/uploads");
        context.Request.Headers["X-Internal-Api-Key"] = ApiKey;
        var caller = new CallerClientAccessor();

        await middleware.InvokeAsync(context, caller);

        Assert.Equal("hooviepack", caller.Client?.Id);
        Assert.Equal("files", caller.Client?.KeyPrefix);
    }

    private static ClientAuthenticationMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, ClientRegistry.Create(
            [new ClientOptions { Id = "hooviepack", ApiKey = ApiKey, KeyPrefix = "files" }],
            legacyApiKey: null,
            TestClients.StorageOptions()));

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
