using eShop.EventBus.Abstractions;
using eShop.PaymentProcessor;
using eShop.PaymentProcessor.IntegrationEvents.EventHandling;
using eShop.PaymentProcessor.IntegrationEvents.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;

namespace eShop.Application.UnitTests;

[TestClass]
public class PaymentProcessorTests
{
    [TestMethod]
    public async Task PublishesConfiguredPaymentOutcome()
    {
        foreach (var paymentSucceeded in new[] { true, false })
        {
            var eventBus = Substitute.For<IEventBus>();
            var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
            options.CurrentValue.Returns(new PaymentOptions { PaymentSucceeded = paymentSucceeded });
            var gateway = new ConfiguredPaymentGateway(options);
            var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
                eventBus, gateway, NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);

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
        }
    }

    [TestMethod]
    public async Task RepeatedHandlingUsesOneConfiguredGatewayCharge()
    {
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        var optionReads = 0;
        options.CurrentValue.Returns(_ =>
        {
            Interlocked.Increment(ref optionReads);
            return new PaymentOptions { PaymentSucceeded = true };
        });
        var gateway = new ConfiguredPaymentGateway(options);
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus, gateway, NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);
        var stockConfirmed = new OrderStatusChangedToStockConfirmedIntegrationEvent(42);

        await Task.WhenAll(handler.Handle(stockConfirmed), handler.Handle(stockConfirmed));

        Assert.AreEqual(1, optionReads, "The configured gateway should resolve one logical charge for the order key.");
        await eventBus.Received(2).PublishAsync(Arg.Is<OrderPaymentSucceededIntegrationEvent>(e => e.OrderId == 42));
    }

    [TestMethod]
    public async Task RetriesTimeoutsWithBackoffAndStableIdempotencyKey()
    {
        var eventBus = Substitute.For<IEventBus>();
        var gateway = Substitute.For<IPaymentGateway>();
        var keys = new List<string>();
        gateway.ChargeAsync(42, Arg.Any<string>()).Returns(async call =>
        {
            keys.Add(call.Arg<string>());
            if (keys.Count <= 2)
            {
                await Task.Delay(30);
                if (keys.Count == 1) throw new TimeoutException();
            }
            return true;
        });
        var logger = Substitute.For<ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>>();
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(eventBus, gateway, logger);

        var fullDuration = Stopwatch.StartNew();
        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));
        fullDuration.Stop();
        var loggedGatewayDuration = GetLoggedGatewayLatencyMilliseconds(logger);
        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));

        Assert.HasCount(3, keys);
        Assert.AreEqual("order-42", keys[0]);
        Assert.AreEqual(keys[0], keys[1]);
        Assert.AreEqual(keys[0], keys[2]);
        await eventBus.Received(2).PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
        Assert.IsTrue(loggedGatewayDuration >= 40, $"Expected both gateway attempts to contribute to latency; observed {loggedGatewayDuration} ms.");
        Assert.IsTrue(fullDuration.Elapsed.TotalMilliseconds - loggedGatewayDuration >= 7,
            "Logged gateway latency should exclude the retry backoff delay.");
    }

    [TestMethod]
    public async Task TimeoutExhaustionDoesNotPublishPaymentOutcome()
    {
        var eventBus = Substitute.For<IEventBus>();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ChargeAsync(42, Arg.Any<string>()).Returns(async _ =>
        {
            await Task.Delay(15);
            return await Task.FromException<bool>(new TimeoutException());
        });
        var logger = Substitute.For<ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>>();
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus, gateway, logger);

        var fullDuration = Stopwatch.StartNew();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42)));
        fullDuration.Stop();

        await gateway.Received(3).ChargeAsync(42, "order-42");
        await eventBus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
        await eventBus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
        var loggedGatewayDuration = GetLoggedGatewayLatencyMilliseconds(logger);
        Assert.IsTrue(loggedGatewayDuration >= 40, $"Expected all failed gateway attempts to contribute to latency; observed {loggedGatewayDuration} ms.");
        Assert.IsTrue(fullDuration.Elapsed.TotalMilliseconds - loggedGatewayDuration >= 20,
            "Logged gateway latency on exhaustion should exclude retry backoff delays.");
    }

    private static double GetLoggedGatewayLatencyMilliseconds(
        ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler> logger)
    {
        var message = logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == "Log")
            .Select(call => call.GetArguments()[2]?.ToString())
            .FirstOrDefault(text => text?.Contains("Payment gateway call for order 42 took") == true);
        Assert.IsNotNull(message);
        var start = message!.IndexOf(" took ", StringComparison.Ordinal) + " took ".Length;
        var end = message.IndexOf(" ms", start, StringComparison.Ordinal);
        return double.Parse(message[start..end], CultureInfo.InvariantCulture);
    }
}
