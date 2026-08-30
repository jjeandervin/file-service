using System.Security.Cryptography;
using System.Text;
using HooviePack.Files.Api.Configuration;
using Microsoft.Extensions.Options;

namespace HooviePack.Files.Api.Infrastructure.Security;

public sealed class InternalApiKeyMiddleware(
    RequestDelegate next,
    IOptions<InternalApiOptions> options)
{
    private const string HeaderName = "X-Internal-Api-Key";
    private readonly byte[] _configuredKeyHash = Hash(options.Value.ApiKey);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/files"))
        {
            await next(context);
            return;
        }

        var suppliedKeyHash = Hash(context.Request.Headers[HeaderName].ToString());
        if (!CryptographicOperations.FixedTimeEquals(_configuredKeyHash, suppliedKeyHash))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    type = "about:blank",
                    title = "Unauthorized",
                    status = StatusCodes.Status401Unauthorized,
                    detail = "A valid internal service credential is required.",
                    traceId = context.TraceIdentifier
                },
                context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
}
