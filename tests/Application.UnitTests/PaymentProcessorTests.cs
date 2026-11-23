using eShop.EventBus.Abstractions;
using eShop.PaymentProcessor;
using eShop.PaymentProcessor.Gateway;
using eShop.PaymentProcessor.Payments;
using eShop.PaymentProcessor.IntegrationEvents.EventHandling;
using eShop.PaymentProcessor.IntegrationEvents.Events;
using Microsoft.Extensions.Hosting;
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
        var bus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions());
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PaymentResult(Guid.NewGuid(), paymentSucceeded)));
        var service = new PaymentService(gateway, options, TimeProvider.System, NullLogger<PaymentService>.Instance);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(bus, service, lifetime,
            NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);
        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));
        await gateway.Received(1).PayAsync(42, "order-42", Arg.Any<CancellationToken>());
        if (paymentSucceeded)
        {
            await bus.Received(1).PublishAsync(Arg.Is<OrderPaymentSucceededIntegrationEvent>(e => e.OrderId == 42));
            await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
        }
        else
        {
            await bus.Received(1).PublishAsync(Arg.Is<OrderPaymentFailedIntegrationEvent>(e => e.OrderId == 42));
            await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
        }
    }

    [TestMethod]
    public async Task TimeoutsAreRetriedWithSameKey()
    {
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero });
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(
            _ => throw new TimeoutException(), _ => throw new TimeoutException(),
            _ => Task.FromResult(new PaymentResult(Guid.NewGuid(), true)));
        var service = new PaymentService(gateway, options, TimeProvider.System, NullLogger<PaymentService>.Instance);
        var result = await service.ProcessAsync(42, CancellationToken.None);
        Assert.IsTrue(result!.Succeeded);
        await gateway.Received(3).PayAsync(42, "order-42", Arg.Any<CancellationToken>());
    }
    [TestMethod]
    public async Task AttemptDeadlinesExhaustionAndCancellationAreDistinct()
    {
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { AttemptTimeout = TimeSpan.FromMilliseconds(30), InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero });
        var gateway = Substitute.For<IPaymentGateway>();
        var cancellations = 0;
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(async call =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2)); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancellations); throw; }
            return new PaymentResult(Guid.NewGuid(), true);
        });
        var service = new PaymentService(gateway, options, TimeProvider.System, NullLogger<PaymentService>.Instance);
        Assert.IsNull(await service.ProcessAsync(42, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(3, cancellations);
        await gateway.Received(3).PayAsync(42, "order-42", Arg.Any<CancellationToken>());
        gateway.ClearReceivedCalls();
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.ProcessAsync(42, shutdown.Token));
        await gateway.Received(1).PayAsync(42, "order-42", Arg.Any<CancellationToken>());
        gateway.ClearReceivedCalls();
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<PaymentResult>(new InvalidOperationException()));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await service.ProcessAsync(42, CancellationToken.None));
        await gateway.Received(1).PayAsync(42, "order-42", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task LogsLatencyForEveryGatewayAttempt()
    {
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero });
        var gateway = Substitute.For<IPaymentGateway>();
        var clock = new PaymentClock();
        var logger = new RecordingPaymentLogger();
        var calls = 0;
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(37));
            if (++calls == 1) throw new TimeoutException();
            return Task.FromResult(new PaymentResult(Guid.NewGuid(), false));
        });
        var service = new PaymentService(gateway, options, clock, logger);
        var result = await service.ProcessAsync(42, CancellationToken.None);
        Assert.IsFalse(result!.Succeeded);
        var records = logger.Records.Select(r => r.ToDictionary(x => x.Key, x => x.Value)).Where(r => r.ContainsKey("ElapsedMilliseconds")).ToArray();
        Assert.AreEqual(2, records.Length);
        for (var i = 0; i < records.Length; i++)
        {
            Assert.AreEqual(42, records[i]["OrderId"]);
            Assert.AreEqual("order-42", records[i]["IdempotencyKey"]);
            Assert.AreEqual(i + 1, records[i]["Attempt"]);
            Assert.AreEqual(37d, records[i]["ElapsedMilliseconds"]);
        }
        Assert.AreEqual("timeout", records[0]["Outcome"]);
        Assert.AreEqual("declined", records[1]["Outcome"]);
    }

    [TestMethod]
    public async Task TimeoutsUseBoundedExponentialBackoff()
    {
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { InitialBackoff = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromMilliseconds(150), MaxAttempts = 4 });
        var gateway = Substitute.For<IPaymentGateway>();
        var starts = new List<TimeSpan>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            starts.Add(watch.Elapsed);
            if (starts.Count < 4) throw new TimeoutException();
            return Task.FromResult(new PaymentResult(Guid.NewGuid(), true));
        });
        var service = new PaymentService(gateway, options, TimeProvider.System, NullLogger<PaymentService>.Instance);
        Assert.IsTrue((await service.ProcessAsync(42, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))!.Succeeded);
        Assert.HasCount(4, starts);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90), starts[1] - starts[0]);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140), starts[2] - starts[1]);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140), starts[3] - starts[2]);
    }
    [TestMethod]
    public async Task HandlerRetriesOnlyAfterControlledBackoff()
    {
        var clock = new ManualPaymentTime();
        var bus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions());
        var gateway = Substitute.For<IPaymentGateway>();
        var entries = new List<long>();
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entries.Add(clock.GetTimestamp());
            if (entries.Count < 3) throw new TimeoutException();
            return Task.FromResult(new PaymentResult(Guid.NewGuid(), true));
        });
        var service = new PaymentService(gateway, options, clock, NullLogger<PaymentService>.Instance);
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(bus, service,
            Substitute.For<IHostApplicationLifetime>(), NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);
        var pending = handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));
        Assert.HasCount(1, entries);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.HasCount(1, entries);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.HasCount(2, entries);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.HasCount(2, entries);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(3, entries);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250).Ticks, entries[1]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(750).Ticks, entries[2]);
        await bus.Received(1).PublishAsync(Arg.Is<OrderPaymentSucceededIntegrationEvent>(e => e.OrderId == 42));
        await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
    }

    [TestMethod]
    public async Task LogsSuccessCancellationAndExhaustion()
    {
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero });
        var clock = new PaymentClock();
        var logger = new RecordingPaymentLogger();
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(12));
            return Task.FromResult(new PaymentResult(Guid.NewGuid(), true));
        });
        var service = new PaymentService(gateway, options, clock, logger);
        Assert.IsTrue((await service.ProcessAsync(42, CancellationToken.None))!.Succeeded);
        var record = logger.Records.Single(r => r.Any(x => x.Key == "Outcome")).ToDictionary(x => x.Key, x => x.Value);
        Assert.AreEqual("success", record["Outcome"]);
        Assert.AreEqual(12d, record["ElapsedMilliseconds"]);
        logger.Records.Clear();
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<PaymentResult>(new TimeoutException()));
        var bus = Substitute.For<IEventBus>();
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(bus, service,
            Substitute.For<IHostApplicationLifetime>(), NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);
        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(42));
        await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
        await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
        Assert.AreEqual(2, logger.Records.Count(r => r.Any(x => x.Key == "Outcome" && Equals(x.Value, "timeout"))));
        Assert.IsTrue(logger.Records.Any(r => r.Any(x => x.Key == "{OriginalFormat}" && x.Value?.ToString()?.Contains("exhausted") == true)));
        logger.Records.Clear();
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(_ => Task.FromCanceled<PaymentResult>(stopping.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.ProcessAsync(42, stopping.Token));
        Assert.IsTrue(logger.Records.Any(r => r.Any(x => x.Key == "Outcome" && Equals(x.Value, "cancelled"))));
    }
    [TestMethod]
    public async Task AttemptLatencyExcludesBackoff()
    {
        var options = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        options.CurrentValue.Returns(new PaymentOptions());
        var clock = new ManualPaymentTime();
        var logger = new RecordingPaymentLogger();
        var gateway = Substitute.For<IPaymentGateway>();
        var attempts = 0;
        gateway.PayAsync(42, "order-42", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(37));
            if (++attempts == 1) throw new TimeoutException();
            return Task.FromResult(new PaymentResult(Guid.NewGuid(), true));
        });
        var service = new PaymentService(gateway, options, clock, logger);
        var pending = service.ProcessAsync(42, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.IsTrue((await pending.WaitAsync(TimeSpan.FromSeconds(10)))!.Succeeded);
        var records = logger.Records.Select(r => r.ToDictionary(x => x.Key, x => x.Value)).Where(r => r.ContainsKey("ElapsedMilliseconds")).ToArray();
        Assert.HasCount(2, records);
        foreach (var record in records)
        {
            Assert.AreEqual(37d, record["ElapsedMilliseconds"]);
            Assert.IsFalse(record.Keys.Any(k => k.Contains("card", StringComparison.OrdinalIgnoreCase)));
        }
    }}
