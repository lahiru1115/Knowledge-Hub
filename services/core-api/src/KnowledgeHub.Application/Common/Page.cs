namespace KnowledgeHub.Application.Common;

/// <summary>A page of a list in the shape every list endpoint returns (IR-14, FR-SRCH-04).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int Total);
