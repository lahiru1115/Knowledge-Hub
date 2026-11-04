namespace KnowledgeHub.Application.Common;

/// <summary>Collects per-field validation messages and raises one <c>400 validation_failed</c> with all of them.</summary>
public sealed class FieldErrors
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
    }

    public void ThrowIfAny()
    {
        if (!Any) return;
        throw new AppException(ProblemCode.ValidationFailed, "One or more fields are not valid.")
        {
            Errors = _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
        };
    }
}
