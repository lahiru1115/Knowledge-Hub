using KnowledgeHub.Application.Common;
using Xunit;

namespace KnowledgeHub.UnitTests;

public class ProblemCatalogueTests
{
    /// <summary>Doc 02 §8.4: every code, its HTTP status.</summary>
    [Theory]
    [InlineData(ProblemCode.ValidationFailed, "validation_failed", 400)]
    [InlineData(ProblemCode.InvalidToken, "invalid_token", 400)]
    [InlineData(ProblemCode.Unauthenticated, "unauthenticated", 401)]
    [InlineData(ProblemCode.InvalidCredentials, "invalid_credentials", 401)]
    [InlineData(ProblemCode.CsrfFailed, "csrf_failed", 403)]
    [InlineData(ProblemCode.Forbidden, "forbidden", 403)]
    [InlineData(ProblemCode.AccountDisabled, "account_disabled", 403)]
    [InlineData(ProblemCode.NotFound, "not_found", 404)]
    [InlineData(ProblemCode.EmailTaken, "email_taken", 409)]
    [InlineData(ProblemCode.CollectionNameTaken, "collection_name_taken", 409)]
    [InlineData(ProblemCode.TagNameTaken, "tag_name_taken", 409)]
    [InlineData(ProblemCode.EditConflict, "edit_conflict", 409)]
    [InlineData(ProblemCode.NotEnoughContent, "not_enough_content", 422)]
    [InlineData(ProblemCode.AccountLocked, "account_locked", 423)]
    [InlineData(ProblemCode.RateLimited, "rate_limited", 429)]
    [InlineData(ProblemCode.AiQuotaExceeded, "ai_quota_exceeded", 429)]
    [InlineData(ProblemCode.InternalError, "internal_error", 500)]
    [InlineData(ProblemCode.AiUnavailable, "ai_unavailable", 503)]
    [InlineData(ProblemCode.ServiceUnavailable, "service_unavailable", 503)]
    public void Codes_and_statuses_match_the_error_catalogue(ProblemCode code, string text, int status)
    {
        Assert.Equal(text, ProblemCatalogue.Code(code));
        Assert.Equal(status, ProblemCatalogue.Status(code));
        Assert.False(string.IsNullOrWhiteSpace(ProblemCatalogue.Title(code)));
    }

    [Fact]
    public void Every_code_in_the_enum_is_covered_by_the_contract()
    {
        var contract = File.ReadAllText(FindContract());
        foreach (var code in Enum.GetValues<ProblemCode>())
            Assert.Contains($"- {ProblemCatalogue.Code(code)}", contract, StringComparison.Ordinal);
    }

    private static string FindContract()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "contracts", "openapi.yaml"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "openapi.yaml");
    }
}
