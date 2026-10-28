namespace eShop.PaymentProcessor.IntegrationEvents.EventHandling;

public class OrderStatusChangedToStockConfirmedIntegrationEventHandler(
    IEventBus eventBus,
    PaymentService paymentService,
    ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler> logger) :
    IIntegrationEventHandler<OrderStatusChangedToStockConfirmedIntegrationEvent>
{
    public async Task Handle(OrderStatusChangedToStockConfirmedIntegrationEvent @event)
    {
        await Handle(@event, CancellationToken.None);
    }

    public async Task Handle(OrderStatusChangedToStockConfirmedIntegrationEvent @event, CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);

        var result = await paymentService.ChargeAsync(@event.OrderId, cancellationToken);
        if (result == PaymentResult.Unknown) return;
        IntegrationEvent orderPaymentIntegrationEvent = result == PaymentResult.Succeeded
            ? new OrderPaymentSucceededIntegrationEvent(@event.OrderId)
            : new OrderPaymentFailedIntegrationEvent(@event.OrderId);

        logger.LogInformation("Publishing integration event: {IntegrationEventId} - ({@IntegrationEvent})", orderPaymentIntegrationEvent.Id, orderPaymentIntegrationEvent);

        await eventBus.PublishAsync(orderPaymentIntegrationEvent);
    }
}
