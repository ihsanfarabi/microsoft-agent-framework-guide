namespace MafDemo.Core.Domain;

public enum TicketStatus { Open, InProgress, Resolved, Closed }

public enum TicketPriority { Low, Normal, High, Critical }

public record Ticket(Guid Id, string Title, string Description,
    TicketPriority Priority, TicketStatus Status, string? Assignee,
    DateTimeOffset CreatedAt, IReadOnlyList<string> Notes);