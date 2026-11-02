namespace KnowledgeHub.Application;

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface ICurrentUser { Guid? UserId { get; } bool IsAdmin { get; } }
