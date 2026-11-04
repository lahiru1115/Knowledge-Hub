using System.Text.RegularExpressions;

namespace KnowledgeHub.Application.Common;

/// <summary>
/// Removes what must never reach a log line (NFR-PRIV-04, Doc 02 §11): tokens, cookies, passwords and content fields,
/// and masks email addresses. It works on finished text, so it also covers values that were logged by accident.
/// </summary>
public static partial class LogRedactor
{
    public const string Redacted = "[redacted]";

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bBearer\s+[^\s,;""']+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    // Cookies and headers that carry credentials: name=value or name: value.
    [GeneratedRegex(@"\b(kh_at|kh_rt|kh_csrf|x-csrf-token|authorization|set-cookie|cookie)\b(\s*[=:]\s*)[^;\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialHeader();

    // Fields that hold secrets or the user's own content, as key=value, key: value or JSON "key":"value".
    [GeneratedRegex(@"(?<k>""?\b(?:password|new_password|current_password|passwd|secret|token|api[_-]?key|apikey|notes|content|summary|description|text)\b""?)(?<sep>\s*[=:]\s*)(?!\[redacted\])(?<v>""(?:[^""\\]|\\.)*""|[^&\s,;}\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveField();

    // Keep the first character and the domain: enough to tell cases apart, not enough to identify anyone.
    [GeneratedRegex(@"\b([A-Za-z0-9])[A-Za-z0-9._%+-]*@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)")]
    private static partial Regex Email();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = Jwt().Replace(text, Redacted);
        text = Bearer().Replace(text, "Bearer " + Redacted);
        text = CredentialHeader().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
        text = SensitiveField().Replace(text, m =>
            m.Groups["k"].Value + m.Groups["sep"].Value + (m.Groups["v"].Value.StartsWith('"') ? "\"" + Redacted + "\"" : Redacted));
        return Email().Replace(text, "$1***@$2");
    }
}
