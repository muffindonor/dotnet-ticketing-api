namespace Ticketing.Api.Domain;

public enum UserRole { Customer = 0, Admin = 1 }

public enum SeatStatus { Available = 0, Held = 1, Sold = 2 }

// The numeric values are used by the filtered unique index on Orders. Do not renumber them.
public enum OrderStatus
{
    Pending = 0,
    PaymentProcessing = 1,
    Paid = 2,
    Expired = 3,
    PaymentFailed = 4,
}

public sealed class User
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
}

public sealed class Event
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Venue { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public List<Seat> Seats { get; set; } = [];
}

public sealed class Seat
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public required string Label { get; set; }
    public long PriceCents { get; set; }
    public SeatStatus Status { get; set; }
    public Guid? CurrentOrderId { get; set; }
}

public sealed class Order
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SeatId { get; set; }
    public long AmountCents { get; set; }
    public OrderStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? PaymentStartedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>A message saved in the same transaction as the state change that caused it.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid? OrderId { get; set; }
    public required string Destination { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
