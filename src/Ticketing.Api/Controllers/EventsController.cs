using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

[ApiController]
[Route("api/events")]
public sealed class EventsController(
    TicketingDbContext db,
    ISeatHoldService holds,
    TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<EventSummaryResponse>> List(
        [FromQuery, Range(1, 100_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return await db.Events.AsNoTracking()
            .Where(e => e.StartsAt > now)
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new EventSummaryResponse(e.Id, e.Name, e.Venue, e.StartsAt))
            .ToListAsync(ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EventResponse>> Get(Guid id, CancellationToken ct)
    {
        var entity = await db.Events.AsNoTracking().Include(e => e.Seats)
            .SingleOrDefaultAsync(e => e.Id == id, ct);
        return entity is null ? NotFound() : ToResponse(entity);
    }

    [HttpPost]
    [Authorize(Roles = nameof(UserRole.Admin))]
    public async Task<ActionResult<EventResponse>> Create(CreateEventRequest request, CancellationToken ct)
    {
        if (request.StartsAt <= time.GetUtcNow())
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Event must start in the future.");
        }

        // Matches how the database compares labels, where "a1" and "A1" are the same.
        var seats = request.Seats.Select(s => s with { Label = s.Label.Trim() }).ToList();
        var duplicateLabels = seats.GroupBy(s => s.Label, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        if (duplicateLabels)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Seat labels must be unique.");
        }

        var entity = new Event
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Venue = request.Venue,
            StartsAt = request.StartsAt,
            Seats = seats
                .Select(s => new Seat
                {
                    Id = Guid.NewGuid(),
                    Label = s.Label,
                    PriceCents = s.PriceCents,
                    Status = SeatStatus.Available,
                })
                .ToList(),
        };
        db.Events.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (TicketingDbContext.IsUniqueViolation(ex))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Seat labels must be unique.");
        }

        return CreatedAtAction(nameof(Get), new { id = entity.Id }, ToResponse(entity));
    }

    [HttpPost("{eventId:guid}/seats/{seatId:guid}/hold")]
    [Authorize]
    public async Task<ActionResult<OrderResponse>> Hold(Guid eventId, Guid seatId, CancellationToken ct)
    {
        var result = await holds.HoldAsync(eventId, seatId, User.GetUserId(), ct);
        return result.Outcome switch
        {
            HoldOutcome.Held => Created($"/api/orders/{result.Order!.Id}", OrderResponse.From(result.Order)),
            HoldOutcome.SeatTaken => Problem(statusCode: StatusCodes.Status409Conflict, title: "Seat is not available."),
            HoldOutcome.EventStarted => Problem(statusCode: StatusCodes.Status409Conflict, title: "Event has already started."),
            _ => NotFound(),
        };
    }

    private static EventResponse ToResponse(Event e) => new(
        e.Id,
        e.Name,
        e.Venue,
        e.StartsAt,
        e.Seats.OrderBy(s => s.Label)
            .Select(s => new SeatResponse(s.Id, s.Label, s.PriceCents, s.Status))
            .ToList());
}
