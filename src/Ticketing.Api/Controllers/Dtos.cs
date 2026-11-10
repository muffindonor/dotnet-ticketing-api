using System.ComponentModel.DataAnnotations;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Controllers;

public sealed record CredentialsRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password);

public sealed record TokenResponse(string Token);

public sealed record SeatRequest(
    [Required, MaxLength(32)] string Label,
    [Range(0, 100_000_000)] long PriceCents);

public sealed record CreateEventRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(200)] string Venue,
    DateTimeOffset StartsAt,
    [Required, MinLength(1), MaxLength(5000)] List<SeatRequest> Seats);

public sealed record SeatResponse(Guid Id, string Label, long PriceCents, SeatStatus Status);

public sealed record EventSummaryResponse(Guid Id, string Name, string Venue, DateTimeOffset StartsAt);

public sealed record EventResponse(
    Guid Id, string Name, string Venue, DateTimeOffset StartsAt, IReadOnlyList<SeatResponse> Seats);

public sealed record OrderResponse(
    Guid Id,
    Guid SeatId,
    long AmountCents,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? PaidAt,
    string? FailureReason)
{
    public static OrderResponse From(Order o) =>
        new(o.Id, o.SeatId, o.AmountCents, o.Status, o.CreatedAt, o.ExpiresAt, o.PaidAt, o.FailureReason);
}

public sealed record PayRequest([Required, MaxLength(100)] string PaymentToken);
