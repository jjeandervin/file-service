using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HooviePack.Files.Api.Application;

public sealed class FileApiException(int statusCode, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string Detail { get; } = detail;

    public static FileApiException BadRequest(string detail) =>
        new(StatusCodes.Status400BadRequest, "Validation failed", detail);

    public static FileApiException NotFound() =>
        new(StatusCodes.Status404NotFound, "Not found", "The requested file was not found.");

    public static FileApiException Conflict(string detail) =>
        new(StatusCodes.Status409Conflict, "Upload incomplete", detail);
}

public sealed class ObjectStorageUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class FileApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<FileApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }

            return true;
        }

        ProblemDetails problem;
        if (exception is FileApiException apiException)
        {
            problem = new ProblemDetails
            {
                Status = apiException.StatusCode,
                Title = apiException.Title,
                Detail = apiException.Detail,
                Instance = httpContext.Request.Path
            };
        }
        else if (exception is ObjectStorageUnavailableException)
        {
            logger.LogWarning(exception, "S3 operation failed for {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Storage unavailable",
                Detail = "File storage is temporarily unavailable.",
                Instance = httpContext.Request.Path
            };
        }
        else
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred",
                Detail = "The request could not be completed.",
                Instance = httpContext.Request.Path
            };
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        var written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
        if (!written && !HttpMethods.IsHead(httpContext.Request.Method))
        {
            httpContext.Response.ContentType = "application/problem+json";
            await httpContext.Response.WriteAsJsonAsync(problem, CancellationToken.None);
        }

        return true;
    }
}
