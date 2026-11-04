namespace KnowledgeHub.Application.Common;

/// <summary>Machine-readable error codes (Doc 02 §8.4).</summary>
public enum ProblemCode
{
    ValidationFailed, InvalidToken, Unauthenticated, InvalidCredentials, CsrfFailed, Forbidden,
    AccountDisabled, NotFound, EmailTaken, CollectionNameTaken, TagNameTaken, EditConflict,
    NotEnoughContent, AccountLocked, RateLimited, AiQuotaExceeded, InternalError, AiUnavailable,
    ServiceUnavailable,
}

public static class ProblemCatalogue
{
    public static string Code(ProblemCode code) => code switch
    {
        ProblemCode.ValidationFailed => "validation_failed",
        ProblemCode.InvalidToken => "invalid_token",
        ProblemCode.Unauthenticated => "unauthenticated",
        ProblemCode.InvalidCredentials => "invalid_credentials",
        ProblemCode.CsrfFailed => "csrf_failed",
        ProblemCode.Forbidden => "forbidden",
        ProblemCode.AccountDisabled => "account_disabled",
        ProblemCode.NotFound => "not_found",
        ProblemCode.EmailTaken => "email_taken",
        ProblemCode.CollectionNameTaken => "collection_name_taken",
        ProblemCode.TagNameTaken => "tag_name_taken",
        ProblemCode.EditConflict => "edit_conflict",
        ProblemCode.NotEnoughContent => "not_enough_content",
        ProblemCode.AccountLocked => "account_locked",
        ProblemCode.RateLimited => "rate_limited",
        ProblemCode.AiQuotaExceeded => "ai_quota_exceeded",
        ProblemCode.InternalError => "internal_error",
        ProblemCode.AiUnavailable => "ai_unavailable",
        ProblemCode.ServiceUnavailable => "service_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    public static int Status(ProblemCode code) => code switch
    {
        ProblemCode.ValidationFailed or ProblemCode.InvalidToken => 400,
        ProblemCode.Unauthenticated or ProblemCode.InvalidCredentials => 401,
        ProblemCode.CsrfFailed or ProblemCode.Forbidden or ProblemCode.AccountDisabled => 403,
        ProblemCode.NotFound => 404,
        ProblemCode.EmailTaken or ProblemCode.CollectionNameTaken or ProblemCode.TagNameTaken
            or ProblemCode.EditConflict => 409,
        ProblemCode.NotEnoughContent => 422,
        ProblemCode.AccountLocked => 423,
        ProblemCode.RateLimited or ProblemCode.AiQuotaExceeded => 429,
        ProblemCode.InternalError => 500,
        ProblemCode.AiUnavailable or ProblemCode.ServiceUnavailable => 503,
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    public static string Title(ProblemCode code) => code switch
    {
        ProblemCode.ValidationFailed => "The request is not valid.",
        ProblemCode.InvalidToken => "The link is invalid or has expired.",
        ProblemCode.Unauthenticated => "You need to sign in.",
        ProblemCode.InvalidCredentials => "The email or password is not correct.",
        ProblemCode.CsrfFailed => "The request could not be verified.",
        ProblemCode.Forbidden => "You are not allowed to do this.",
        ProblemCode.AccountDisabled => "This account is disabled.",
        ProblemCode.NotFound => "Not found.",
        ProblemCode.EmailTaken => "This email is already registered.",
        ProblemCode.CollectionNameTaken => "A collection with this name already exists.",
        ProblemCode.TagNameTaken => "A tag with this name already exists.",
        ProblemCode.EditConflict => "This item was changed somewhere else.",
        ProblemCode.NotEnoughContent => "There is not enough text to summarise.",
        ProblemCode.AccountLocked => "Too many failed sign-ins. Try again later.",
        ProblemCode.RateLimited => "Too many requests.",
        ProblemCode.AiQuotaExceeded => "The daily AI limit has been reached.",
        ProblemCode.InternalError => "Something went wrong.",
        ProblemCode.AiUnavailable => "AI features are unavailable right now.",
        ProblemCode.ServiceUnavailable => "The service is unavailable right now.",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}

/// <summary>Thrown by Application services; the API turns it into a Problem Details response.</summary>
public sealed class AppException(ProblemCode code, string? detail = null) : Exception(detail ?? ProblemCatalogue.Title(code))
{
    public ProblemCode Code { get; } = code;
    public string? Detail { get; } = detail;
    public IDictionary<string, string[]>? Errors { get; init; }
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>();

    /// <summary>A <c>400 validation_failed</c> about one field.</summary>
    public static AppException Field(string field, string message) =>
        new(ProblemCode.ValidationFailed, "One or more fields are not valid.")
        {
            Errors = new Dictionary<string, string[]> { [field] = [message] },
        };
}
