namespace eShop.PaymentProcessor.IntegrationEvents.EventHandling;

public class OrderStatusChangedToStockConfirmedIntegrationEventHandler(
    IEventBus eventBus,
    IPaymentGateway gateway,
    ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler> logger) :
    IIntegrationEventHandler<OrderStatusChangedToStockConfirmedIntegrationEvent>
{
    public async Task Handle(OrderStatusChangedToStockConfirmedIntegrationEvent @event)
    {
        logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);

        var gatewayElapsed = TimeSpan.Zero;
        bool paymentSucceeded;

        // Business feature comment:
        // When OrderStatusChangedToStockConfirmed Integration Event is handled.
        // Here we're simulating that we'd be performing the payment against any payment gateway
        // Instead of a real payment we just take the env. var to simulate the payment 
        // The payment can be successful or it can fail

        const int maxAttempts = 3;
        var idempotencyKey = $"order-{@event.OrderId}";
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var attemptStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        paymentSucceeded = await gateway.ChargeAsync(@event.OrderId, idempotencyKey);
                    }
                    finally
                    {
                        gatewayElapsed += attemptStopwatch.Elapsed;
                    }
                    break;
                }
                catch (TimeoutException) when (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10 * (1 << (attempt - 1))));
                }
            }
        }
        catch
        {
            logger.LogInformation("Payment gateway call for order {OrderId} took {ElapsedMilliseconds} ms", @event.OrderId, gatewayElapsed.TotalMilliseconds);
            throw;
        }

        logger.LogInformation("Payment gateway call for order {OrderId} took {ElapsedMilliseconds} ms", @event.OrderId, gatewayElapsed.TotalMilliseconds);
        IntegrationEvent orderPaymentIntegrationEvent = paymentSucceeded
            ? new OrderPaymentSucceededIntegrationEvent(@event.OrderId)
            : new OrderPaymentFailedIntegrationEvent(@event.OrderId);

        logger.LogInformation("Publishing integration event: {IntegrationEventId} - ({@IntegrationEvent})", orderPaymentIntegrationEvent.Id, orderPaymentIntegrationEvent);

        await eventBus.PublishAsync(orderPaymentIntegrationEvent);
    }
}
