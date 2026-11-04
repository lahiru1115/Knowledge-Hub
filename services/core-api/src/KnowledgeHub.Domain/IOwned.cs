namespace KnowledgeHub.Domain;

/// <summary>Data that belongs to one user. Every query on it filters by owner (DR-01, Doc 02 §9.4).</summary>
public interface IOwned
{
    Guid UserId { get; }
}

public static class OwnedExtensions
{
    /// <summary>The one way to start a query on user data, so the owner filter is hard to forget.</summary>
    public static IQueryable<T> OwnedBy<T>(this IQueryable<T> source, Guid userId) where T : IOwned =>
        source.Where(e => e.UserId == userId);
}
