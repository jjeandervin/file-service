using System.Text.Json;
using HooviePack.Files.Api.Configuration;
using HooviePack.Files.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace HooviePack.Files.Api.Tests;

public sealed class InternalApiKeyMiddlewareTests
{
    private const string ApiKey = "test-internal-api-key-with-32-characters";

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

        await middleware.InvokeAsync(context);

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

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("Unauthorized", document.RootElement.GetProperty("title").GetString());
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

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static InternalApiKeyMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, Options.Create(new InternalApiOptions { ApiKey = ApiKey }));

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
