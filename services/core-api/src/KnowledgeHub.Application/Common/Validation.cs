using System.Text.Json;
using FluentValidation;

namespace KnowledgeHub.Application.Common;

public static class Validation
{
    /// <summary>Throws <c>400 validation_failed</c> with an <c>errors</c> map keyed by snake_case field name.</summary>
    public static async Task EnsureValidAsync<T>(this IValidator<T> validator, T value, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(value, ct);
        if (result.IsValid) return;
        throw new AppException(ProblemCode.ValidationFailed, "One or more fields are not valid.")
        {
            Errors = result.Errors
                .GroupBy(e => JsonNamingPolicy.SnakeCaseLower.ConvertName(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()),
        };
    }
}
