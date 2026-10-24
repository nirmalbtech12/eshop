namespace eShop.PaymentProcessor.IntegrationEvents.EventHandling;

public class OrderStatusChangedToStockConfirmedIntegrationEventHandler(
    IEventBus eventBus,
    IPaymentGateway paymentGateway,
    IOptionsMonitor<PaymentOptions> options,
    ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler> logger) :
    IIntegrationEventHandler<OrderStatusChangedToStockConfirmedIntegrationEvent>
{
    public async Task Handle(OrderStatusChangedToStockConfirmedIntegrationEvent @event)
    {
        logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);

        IntegrationEvent orderPaymentIntegrationEvent;

        var paymentSucceeded = false;
        var idempotencyKey = $"order-{@event.OrderId}";
        var retryCount = Math.Max(0, options.CurrentValue.MaxTimeoutRetries);
        for (var attempt = 0; ; attempt++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                paymentSucceeded = await paymentGateway.ChargeAsync(@event.OrderId, idempotencyKey);
                stopwatch.Stop();
                logger.LogInformation("Payment gateway attempt completed for order {OrderId} in {GatewayLatencyMs} ms with outcome {PaymentOutcome}",
                    @event.OrderId, stopwatch.Elapsed.TotalMilliseconds, paymentSucceeded ? "succeeded" : "failed");
                break;
            }
            catch (TimeoutException ex) when (attempt < retryCount)
            {
                stopwatch.Stop();
                logger.LogWarning(ex, "Payment gateway timeout for order {OrderId} after {GatewayLatencyMs} ms; retry {RetryAttempt} of {RetryCount}",
                    @event.OrderId, stopwatch.Elapsed.TotalMilliseconds, attempt + 1, retryCount);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, options.CurrentValue.RetryBackoffMilliseconds) * (attempt + 1)));
            }
            catch (TimeoutException ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Payment gateway timed out for order {OrderId} after {GatewayLatencyMs} ms; retries exhausted",
                    @event.OrderId, stopwatch.Elapsed.TotalMilliseconds);
                break;
            }
        }

        orderPaymentIntegrationEvent = paymentSucceeded
            ? new OrderPaymentSucceededIntegrationEvent(@event.OrderId)
            : new OrderPaymentFailedIntegrationEvent(@event.OrderId);

        logger.LogInformation("Publishing integration event: {IntegrationEventId} - ({@IntegrationEvent})", orderPaymentIntegrationEvent.Id, orderPaymentIntegrationEvent);

        await eventBus.PublishAsync(orderPaymentIntegrationEvent);
    }
}
