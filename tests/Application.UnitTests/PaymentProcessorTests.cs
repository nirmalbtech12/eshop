using eShop.EventBus.Abstractions;
using eShop.PaymentProcessor;
using eShop.PaymentProcessor.IntegrationEvents.EventHandling;
using eShop.PaymentProcessor.IntegrationEvents.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace eShop.Application.UnitTests;

[TestClass]
public class PaymentProcessorTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PublishesConfiguredPaymentOutcome(bool paymentSucceeded)
    {
        var eventBus = Substitute.For<IEventBus>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(Arg.Any<int>(), Arg.Any<string>()).Returns(Task.FromResult(paymentSucceeded));
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { PaymentSucceeded = paymentSucceeded });
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus,
            gateway,
            options,
            NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);

        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));

        await gateway.Received(1).ChargeAsync(42, "order-42");

        if (paymentSucceeded)
        {
            await eventBus.Received(1).PublishAsync(
                Arg.Is<OrderPaymentSucceededIntegrationEvent>(e => e.OrderId == 42));
            await eventBus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
        }
        else
        {
            await eventBus.Received(1).PublishAsync(
                Arg.Is<OrderPaymentFailedIntegrationEvent>(e => e.OrderId == 42));
            await eventBus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
        }
    }

    [TestMethod]
    public async Task RetriesTimeoutWithSameIdempotencyKeyBeforePublishingSuccess()
    {
        var eventBus = Substitute.For<IEventBus>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(42, "order-42").Returns(
            _ => Task.FromException<bool>(new TimeoutException()),
            _ => Task.FromResult(true));
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { MaxTimeoutRetries = 1, RetryBackoffMilliseconds = 0 });
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus, gateway, options,
            NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);

        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));

        await gateway.Received(2).ChargeAsync(42, "order-42");
        await eventBus.Received(1).PublishAsync(Arg.Is<OrderPaymentSucceededIntegrationEvent>(e => e.OrderId == 42));
        await eventBus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
    }

    [TestMethod]
    public async Task RedeliveryAfterGatewayRestartReusesStoredChargeResult()
    {
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { PaymentSucceeded = true });
        var durableStore = new TestPaymentChargeStore();
        var gateway = new SimulatedPaymentGateway(durableStore, options);

        var first = await gateway.ChargeAsync(42, "order-42");
        // A new gateway instance models PaymentProcessor restarting before redelivery.
        gateway = new SimulatedPaymentGateway(durableStore, options);
        options.CurrentValue.Returns(new PaymentOptions { PaymentSucceeded = false });
        var second = await gateway.ChargeAsync(42, "order-42");

        Assert.IsTrue(first);
        Assert.IsTrue(second);
    }

    private sealed class TestPaymentChargeStore : IPaymentChargeStore
    {
        private readonly Dictionary<string, bool> _charges = new();

        public Task<bool> GetOrCreateChargeAsync(int orderId, string idempotencyKey, bool paymentSucceeded)
        {
            if (!_charges.TryGetValue(idempotencyKey, out var result))
            {
                result = paymentSucceeded;
                _charges.Add(idempotencyKey, result);
            }

            return Task.FromResult(result);
        }
    }

    [TestMethod]
    public async Task PublishesFailureAfterTimeoutRetriesAreExhausted()
    {
        var eventBus = Substitute.For<IEventBus>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(42, "order-42").Returns(_ => Task.FromException<bool>(new TimeoutException()));
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { MaxTimeoutRetries = 1, RetryBackoffMilliseconds = 0 });
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus, gateway, options,
            NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);

        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));

        await gateway.Received(2).ChargeAsync(42, "order-42");
        await eventBus.Received(1).PublishAsync(Arg.Is<OrderPaymentFailedIntegrationEvent>(e => e.OrderId == 42));
        await eventBus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
    }
}
