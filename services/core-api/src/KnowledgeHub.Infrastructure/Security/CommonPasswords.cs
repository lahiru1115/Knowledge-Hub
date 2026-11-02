using System.Reflection;
using KnowledgeHub.Application.Identity;

namespace KnowledgeHub.Infrastructure.Security;

/// <summary>
/// Rejects passwords from an embedded list (Doc 02 §9.2 calls for the 100,000 most common passwords).
/// The list shipped here is a short starter set; replace common-passwords.txt with the full list.
/// </summary>
public sealed class CommonPasswords : ICommonPasswords
{
    private readonly HashSet<string> _set;

    public CommonPasswords()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(n => n.EndsWith("common-passwords.txt", StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
            if (line.Trim() is { Length: > 0 } p) _set.Add(p);
    }

    public bool IsCommon(string password) => _set.Contains(password);
}
