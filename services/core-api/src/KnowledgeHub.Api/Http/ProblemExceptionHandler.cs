using System.Text.Json;
using KnowledgeHub.Application.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace KnowledgeHub.Api.Http;

public sealed partial class ProblemExceptionHandler(ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case AppException app:
                await ProblemWriter.WriteAsync(httpContext, app.Code, app.Detail, app.Errors, app.Extensions);
                return true;
            case BadHttpRequestException or JsonException:
                await ProblemWriter.WriteAsync(httpContext, ProblemCode.ValidationFailed, "The request body could not be read.");
                return true;
            default:
                LogUnhandled(exception, httpContext.Request.Method, httpContext.Request.Path);
                await ProblemWriter.WriteAsync(httpContext, ProblemCode.InternalError);
                return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private partial void LogUnhandled(Exception exception, string method, PathString path);
}
