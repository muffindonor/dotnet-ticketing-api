using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ticketing.Contracts;

public static class Queues
{
    public const string PaymentRequested = "payments.requested";
    public const string PaymentCompleted = "payments.completed";
}

/// <summary>Sent by the API (through the outbox) when a customer starts paying.</summary>
/// <param name="ChargeBefore">The worker must refuse to charge after this time.</param>
public sealed record PaymentRequested(
    [property: JsonRequired] Guid OrderId,
    [property: JsonRequired] long AmountCents,
    [property: JsonRequired] string PaymentToken,
    DateTimeOffset? ChargeBefore = null);

/// <summary>Sent by the payment worker once the (simulated) charge has finished.</summary>
public sealed record PaymentCompleted(
    [property: JsonRequired] Guid OrderId,
    [property: JsonRequired] bool Succeeded,
    string? FailureReason = null);

/// <summary>Parses message bodies. A JsonException means the message can never succeed.</summary>
public static class PaymentMessages
{
    public static PaymentRequested ParseRequested(ReadOnlySpan<byte> body)
    {
        var message = JsonSerializer.Deserialize<PaymentRequested>(body)
            ?? throw new JsonException("Empty payment request.");
        if (message.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(message.PaymentToken))
        {
            throw new JsonException("Payment request is missing its order or token.");
        }

        return message;
    }

    public static PaymentCompleted ParseCompleted(ReadOnlySpan<byte> body)
    {
        var message = JsonSerializer.Deserialize<PaymentCompleted>(body)
            ?? throw new JsonException("Empty payment result.");
        if (message.OrderId == Guid.Empty)
        {
            throw new JsonException("Payment result is missing its order.");
        }

        return message;
    }
}
