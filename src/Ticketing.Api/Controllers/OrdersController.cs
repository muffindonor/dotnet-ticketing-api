using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/orders")]
public sealed class OrdersController(TicketingDbContext db, OrderPaymentService payments) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<OrderResponse>> Mine(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        return orders.Select(OrderResponse.From).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> Get(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.UserId == userId, ct);
        return order is null ? NotFound() : OrderResponse.From(order);
    }

    [HttpPost("{id:guid}/pay")]
    public async Task<IActionResult> Pay(Guid id, PayRequest request, CancellationToken ct)
    {
        var outcome = await payments.StartAsync(id, User.GetUserId(), request.PaymentToken, ct);
        return outcome switch
        {
            StartPaymentOutcome.Started => Accepted($"/api/orders/{id}"),
            StartPaymentOutcome.NotPayable => Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Order can no longer be paid."),
            _ => NotFound(),
        };
    }
}
