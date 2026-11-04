namespace KnowledgeHub.Domain;

/// <summary>Why fetching a page's details failed (FR-META-03, FR-META-05). Shown as "Couldn't fetch details".</summary>
public enum MetadataError
{
    /// <summary>The address is not allowed: not http(s) on 80/443, or it points at a private network (SSRF guard).</summary>
    BlockedUrl,
    Timeout,
    /// <summary>The host could not be reached or the connection failed.</summary>
    Unreachable,
    /// <summary>The server answered with an error status.</summary>
    HttpError,
    /// <summary>Not a web page or PDF, so there is nothing to read.</summary>
    NotHtml,
    TooLarge,
    TooManyRedirects,
}

/// <summary>
/// Fields the user has set by hand. Metadata extraction fills only the others, so it never overwrites an edit
/// (FR-RES-03).
/// </summary>
[Flags]
public enum EditedFields
{
    None = 0,
    Title = 1,
    Description = 2,
    Type = 4,
}
