using eShop.Ordering.API.Application.IntegrationEvents.EventHandling;
using eShop.Ordering.API.Application.IntegrationEvents.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace eShop.Ordering.UnitTests.Application;

[TestClass]
public class PaymentOutcomeConsumerTests
{
    [TestMethod]
    public async Task ExistingPaymentEventsKeepTheirCommandContracts()
    {
        var mediator = Substitute.For<IMediator>();
        await new OrderPaymentSucceededIntegrationEventHandler(mediator, NullLogger<OrderPaymentSucceededIntegrationEventHandler>.Instance).Handle(new OrderPaymentSucceededIntegrationEvent(42));
        await mediator.Received(1).Send(Arg.Is<SetPaidOrderStatusCommand>(c => c.OrderNumber == 42));
        await new OrderPaymentFailedIntegrationEventHandler(mediator, NullLogger<OrderPaymentFailedIntegrationEventHandler>.Instance).Handle(new OrderPaymentFailedIntegrationEvent(43));
        await mediator.Received(1).Send(Arg.Is<CancelOrderCommand>(c => c.OrderNumber == 43));
    }
}
