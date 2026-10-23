using eShop.EventBus.Abstractions;
using eShop.PaymentProcessor;
using eShop.PaymentProcessor.IntegrationEvents.EventHandling;
using eShop.PaymentProcessor.IntegrationEvents.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
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
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { PaymentSucceeded = paymentSucceeded, RetryDelayMilliseconds = 0 });
        var gateway = Substitute.For<IPaymentGateway>();
        var retryDelay = Substitute.For<IRetryDelay>();
        gateway.ChargeAsync(42, "order-42").Returns(paymentSucceeded);
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus,
            options,
            gateway,
            retryDelay,
            NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);

        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));

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
        await gateway.Received(1).ChargeAsync(42, "order-42");
    }

    [TestMethod]
    public async Task RetriesTimeoutWithBackoffAndUsesStableIdempotencyKey()
    {
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { MaxTimeoutRetries = 2, RetryDelayMilliseconds = 10 });
        var gateway = Substitute.For<IPaymentGateway>();
        var retryDelay = Substitute.For<IRetryDelay>();
        gateway.ChargeAsync(42, "order-42").Returns(
            _ => Task.FromException<bool>(new TimeoutException()),
            _ => Task.FromException<bool>(new TimeoutException()),
            _ => Task.FromResult(true));
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus, options, gateway, retryDelay,
            NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);

        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));

        await gateway.Received(3).ChargeAsync(42, "order-42");
        await retryDelay.Received(1).DelayAsync(TimeSpan.FromMilliseconds(10));
        await retryDelay.Received(1).DelayAsync(TimeSpan.FromMilliseconds(20));
        await eventBus.Received(1).PublishAsync(Arg.Is<OrderPaymentSucceededIntegrationEvent>(e => e.OrderId == 42));
    }

    [TestMethod]
    public async Task PublishesFailureAfterTimeoutRetriesAreExhausted()
    {
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { MaxTimeoutRetries = 1, RetryDelayMilliseconds = 0 });
        var gateway = Substitute.For<IPaymentGateway>();
        var retryDelay = Substitute.For<IRetryDelay>();
        gateway.ChargeAsync(42, "order-42").Returns(_ => Task.FromException<bool>(new TimeoutException()));
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus, options, gateway, retryDelay,
            NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);

        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));

        await gateway.Received(2).ChargeAsync(42, "order-42");
        await eventBus.Received(1).PublishAsync(Arg.Is<OrderPaymentFailedIntegrationEvent>(e => e.OrderId == 42));
    }

    [TestMethod]
    public async Task RepeatedOrderPassesSameProviderIdempotencyKey()
    {
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        var paymentOptions = new PaymentOptions { PaymentSucceeded = true, SimulatedTimeoutAttempts = 1, RetryDelayMilliseconds = 0 };
        options.CurrentValue.Returns(paymentOptions);
        var resultStore = new TestPaymentResultStore();
        var chargeProcessor = new SimulatedPaymentChargeProcessor(resultStore, options);
        var gateway = new SimulatedPaymentGateway(chargeProcessor);
        var retryDelay = Substitute.For<IRetryDelay>();
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus, options, gateway, retryDelay,
            NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);

        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));
        paymentOptions.PaymentSucceeded = false;
        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));

        Assert.AreEqual(1, resultStore.NewResults);
        Assert.AreEqual(2, resultStore.Attempts);
        await retryDelay.Received(1).DelayAsync(TimeSpan.Zero);
    }

    [TestMethod]
    public async Task LogsLatencyAndPropagatesUnexpectedGatewayFailure()
    {
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions());
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(42, "order-42").Returns(_ => Task.FromException<bool>(new InvalidOperationException("gateway error")));
        var retryDelay = Substitute.For<IRetryDelay>();
        var logger = new TestLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>();
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus, options, gateway, retryDelay, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42)));

        Assert.IsTrue(logger.Messages.Any(message => message.Contains("failed after") && message.Contains("ms")));
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class TestPaymentResultStore : IPaymentResultStore
    {
        private readonly Dictionary<string, bool> _results = [];
        public int NewResults { get; private set; }
        public int Attempts { get; private set; }

        public Task<PaymentAttemptResult> AttemptChargeAsync(string idempotencyKey, bool result, int timeoutsBeforeResult)
        {
            if (_results.TryGetValue(idempotencyKey, out var storedResult))
            {
                return Task.FromResult(new PaymentAttemptResult(TimedOut: false, Succeeded: storedResult));
            }

            Attempts++;
            if (Attempts <= timeoutsBeforeResult)
            {
                return Task.FromResult(new PaymentAttemptResult(TimedOut: true, Succeeded: false));
            }

            NewResults++;
            _results.Add(idempotencyKey, result);
            return Task.FromResult(new PaymentAttemptResult(TimedOut: false, Succeeded: result));
        }
    }
}
