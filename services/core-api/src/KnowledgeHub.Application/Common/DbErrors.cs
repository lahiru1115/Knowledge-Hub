using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Common;

public static class DbErrors
{
    /// <summary>
    /// True for a PostgreSQL unique violation (SQLSTATE 23505), optionally on a named constraint. Read by
    /// reflection so the Application layer does not need to reference the database driver.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex, string? constraintContains = null)
    {
        var inner = ex.InnerException;
        if (inner?.GetType().GetProperty("SqlState")?.GetValue(inner) as string != "23505") return false;
        if (constraintContains is null) return true;
        var constraint = inner.GetType().GetProperty("ConstraintName")?.GetValue(inner) as string;
        return constraint?.Contains(constraintContains, StringComparison.OrdinalIgnoreCase) == true;
    }
}
