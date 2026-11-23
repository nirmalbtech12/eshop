using eShop.PaymentProcessor.Payments;

namespace eShop.PaymentProcessor.IntegrationEvents.EventHandling;

public class OrderStatusChangedToStockConfirmedIntegrationEventHandler(
    IEventBus eventBus, PaymentService paymentService, IHostApplicationLifetime lifetime,
    ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler> logger) :
    IIntegrationEventHandler<OrderStatusChangedToStockConfirmedIntegrationEvent>
{
    public async Task Handle(OrderStatusChangedToStockConfirmedIntegrationEvent @event)
    {
        logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);
        var result = await paymentService.ProcessAsync(@event.OrderId, lifetime.ApplicationStopping);
        if (result is null) return;
        IntegrationEvent outcome = result.Succeeded
            ? new OrderPaymentSucceededIntegrationEvent(@event.OrderId)
            : new OrderPaymentFailedIntegrationEvent(@event.OrderId);
        await eventBus.PublishAsync(outcome);
    }
}
