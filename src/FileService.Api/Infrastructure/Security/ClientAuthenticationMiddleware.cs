using System.Security.Cryptography;

namespace FileService.Api.Infrastructure.Security;

public sealed class ClientAuthenticationMiddleware(
    RequestDelegate next,
    ClientRegistry registry)
{
    private const string HeaderName = "X-Internal-Api-Key";

    public async Task InvokeAsync(HttpContext context, CallerClientAccessor caller)
    {
        if (!context.Request.Path.StartsWithSegments("/files"))
        {
            await next(context);
            return;
        }

        var suppliedKeyHash = ClientRegistry.HashApiKey(context.Request.Headers[HeaderName].ToString());
        FileClient? matched = null;
        // Compare against every client without stopping early, so timing does not reveal which one matched.
        foreach (var client in registry.Clients)
        {
            var isMatch = CryptographicOperations.FixedTimeEquals(client.ApiKeyHash, suppliedKeyHash);
            matched = isMatch ? client : matched;
        }

        if (matched is null)
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

        caller.Client = matched;
        await next(context);
    }
}
