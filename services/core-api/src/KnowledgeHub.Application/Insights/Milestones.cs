using KnowledgeHub.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Insights;

/// <summary>
/// Remembers the first time a user searched and the first time they asked a question, for the onboarding checklist
/// (FR-DASH-05). Saving and creating things can be read off the data; these two leave no trace, so they are noted here.
/// Each call is one cheap update that changes nothing after the first time.
/// </summary>
public sealed class Milestones(IAppDbContext db)
{
    public Task SearchedAsync(Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId && !u.TriedSearch)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TriedSearch, true), ct);

    public Task AskedAsync(Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId && !u.AskedQuestion)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AskedQuestion, true), ct);
}
