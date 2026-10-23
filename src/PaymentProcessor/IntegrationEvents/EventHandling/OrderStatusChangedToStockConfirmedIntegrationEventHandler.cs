namespace eShop.PaymentProcessor.IntegrationEvents.EventHandling;

public class OrderStatusChangedToStockConfirmedIntegrationEventHandler(
    IEventBus eventBus,
    IOptionsMonitor<PaymentOptions> options,
    IPaymentGateway paymentGateway,
    IRetryDelay retryDelay,
    ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler> logger) :
    IIntegrationEventHandler<OrderStatusChangedToStockConfirmedIntegrationEvent>
{
    public async Task Handle(OrderStatusChangedToStockConfirmedIntegrationEvent @event)
    {
        logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);

        var paymentSucceeded = false;
        var maxAttempts = Math.Max(1, options.CurrentValue.MaxTimeoutRetries + 1);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                paymentSucceeded = await paymentGateway.ChargeAsync(@event.OrderId, $"order-{@event.OrderId}");
                stopwatch.Stop();
                logger.LogInformation("Payment gateway attempt {Attempt} for order {OrderId} completed in {ElapsedMilliseconds} ms with outcome {Outcome}",
                    attempt, @event.OrderId, stopwatch.ElapsedMilliseconds, paymentSucceeded ? "succeeded" : "failed");
                break;
            }
            catch (TimeoutException ex) when (attempt < maxAttempts)
            {
                stopwatch.Stop();
                logger.LogWarning(ex, "Payment gateway attempt {Attempt} for order {OrderId} timed out after {ElapsedMilliseconds} ms",
                    attempt, @event.OrderId, stopwatch.ElapsedMilliseconds);
                await retryDelay.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(0, options.CurrentValue.RetryDelayMilliseconds) * attempt));
            }
            catch (TimeoutException ex)
            {
                stopwatch.Stop();
                logger.LogWarning(ex, "Payment gateway attempt {Attempt} for order {OrderId} timed out after {ElapsedMilliseconds} ms; retries exhausted",
                    attempt, @event.OrderId, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Payment gateway attempt {Attempt} for order {OrderId} failed after {ElapsedMilliseconds} ms",
                    attempt, @event.OrderId, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        IntegrationEvent orderPaymentIntegrationEvent = paymentSucceeded
            ? new OrderPaymentSucceededIntegrationEvent(@event.OrderId)
            : new OrderPaymentFailedIntegrationEvent(@event.OrderId);

        logger.LogInformation("Publishing integration event: {IntegrationEventId} - ({@IntegrationEvent})", orderPaymentIntegrationEvent.Id, orderPaymentIntegrationEvent);

        await eventBus.PublishAsync(orderPaymentIntegrationEvent);
    }
}
