using eShop.EventBus.Abstractions;
using eShop.PaymentProcessor;
using eShop.PaymentProcessor.IntegrationEvents.EventHandling;
using eShop.PaymentProcessor.IntegrationEvents.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

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
        options.CurrentValue.Returns(new PaymentOptions { PaymentSucceeded = paymentSucceeded });
        var service = new PaymentService(new SimulatedPaymentGateway(options), options, TimeProvider.System, NullLogger<PaymentService>.Instance);
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(
            eventBus,
            service,
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
    }

    [TestMethod]
    public async Task TimeoutsBackOffAndSucceedOnFinalAttempt()
    {
        var options = Monitor(new PaymentOptions { MaxAttempts = 3, AttemptTimeoutMilliseconds = 1000, InitialBackoffMilliseconds = 250 });
        var gateway = new ScriptedGateway((_, _, _) => Task.FromException<bool>(new TimeoutException()), (_, _, _) => Task.FromException<bool>(new TimeoutException()), (_, _, _) => Task.FromResult(true));
        var result = await Service(gateway, options).ChargeAsync(1);
        Assert.AreEqual(PaymentResult.Succeeded, result);
        Assert.AreEqual(3, gateway.Calls);
        CollectionAssert.AreEqual(new[] { "order-payment:1", "order-payment:1", "order-payment:1" }, gateway.Keys);
        var firstBackoff = gateway.StartTimes[1] - gateway.StartTimes[0];
        var secondBackoff = gateway.StartTimes[2] - gateway.StartTimes[1];
        Assert.IsTrue(firstBackoff >= TimeSpan.FromMilliseconds(249), $"Attempt 2 began after only {firstBackoff.TotalMilliseconds} ms; expected the configured 250 ms backoff.");
        Assert.IsTrue(secondBackoff >= TimeSpan.FromMilliseconds(499), $"Attempt 3 began after only {secondBackoff.TotalMilliseconds} ms; expected the configured 500 ms backoff.");
    }

    [TestMethod]
    public async Task ExhaustedTimeoutsLeavePaymentUnconfirmed()
    {
        var options = Monitor(new PaymentOptions { MaxAttempts = 3, AttemptTimeoutMilliseconds = 30, InitialBackoffMilliseconds = 1 });
        var bus = Substitute.For<IEventBus>();
        var pending = new List<TaskCompletionSource<bool>>();
        var gateway = new ScriptedGateway((_, _, _) =>
        {
            var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(response);
            return response.Task;
        });
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(bus, Service(gateway, options), NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);
        var timer = Stopwatch.StartNew();
        try
        {
            await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(5));
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(2), "Timeout exhaustion exceeded its bounded deadline.");
            Assert.AreEqual(3, gateway.Calls, "Exhaustion must stop after exactly three attempts.");
            Assert.AreEqual(3, pending.Count);
            await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
            await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
        }
        finally
        {
            foreach (var response in pending) response.TrySetResult(true);
            await Task.WhenAll(pending.Select(response => response.Task));
        }
    }

    [TestMethod]
    public async Task CommittedChargeWithLostResponseIsNotChargedTwice()
    {
        var opts = Monitor(new PaymentOptions { PaymentSucceeded = true, MaxAttempts = 2, AttemptTimeoutMilliseconds = 10, InitialBackoffMilliseconds = 1 });
        var simulator = new SimulatedPaymentGateway(opts);
        var gateway = new ScriptedGateway(async (id, key, ct) => { await simulator.ChargeAsync(id, key, ct); throw new TimeoutException(); }, (id, key, ct) => simulator.ChargeAsync(id, key, ct));
        Assert.AreEqual(PaymentResult.Succeeded, await Service(gateway, opts).ChargeAsync(9));
        Assert.AreEqual(1, simulator.ChargeCount);
        Assert.AreEqual(2, gateway.Calls);
        Assert.AreEqual(gateway.Keys[0], gateway.Keys[1]);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateEventsShareOneCharge()
    {
        var configured = new PaymentOptions { PaymentSucceeded = false };
        var opts = Monitor(configured);
        var gateway = new SimulatedPaymentGateway(opts);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 32).Select(async _ =>
        {
            await start.Task;
            return await gateway.ChargeAsync(11, "order-payment:11", CancellationToken.None);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(32, results.Length);
        Assert.IsTrue(results.All(result => result == results[0]));
        var firstResult = results[0];
        Assert.IsFalse(firstResult);
        Assert.AreEqual(1, gateway.ChargeCount);
        configured.PaymentSucceeded = true;
        var duplicateResult = await gateway.ChargeAsync(11, "order-payment:11", CancellationToken.None);
        Assert.AreEqual(firstResult, duplicateResult, "A duplicate key must retain its original committed result after configuration changes.");
        Assert.AreEqual(1, gateway.ChargeCount);
        Assert.IsTrue(await gateway.ChargeAsync(12, "order-payment:12", CancellationToken.None));
        Assert.AreEqual(2, gateway.ChargeCount);
    }

    [TestMethod]
    public async Task NonTimeoutFailuresAndCallerCancellationAreNotRetried()
    {
        var opts = Monitor(new PaymentOptions { MaxAttempts = 3, AttemptTimeoutMilliseconds = 1000, InitialBackoffMilliseconds = 1 });
        var gateway = new ScriptedGateway((_, _, _) => Task.FromException<bool>(new InvalidOperationException()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(gateway, opts).ChargeAsync(1));
        Assert.AreEqual(1, gateway.Calls);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(gateway, opts).ChargeAsync(2, cts.Token));

        await CancellationDuringAttemptDoesNotPublishOrRetry();
        await CancellationDuringBackoffDoesNotPublishOrRetry();
    }

    private static async Task CancellationDuringAttemptDoesNotPublishOrRetry()
    {
        var opts = Monitor(new PaymentOptions { MaxAttempts = 3, AttemptTimeoutMilliseconds = 5000, InitialBackoffMilliseconds = 1 });
        var bus = Substitute.For<IEventBus>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ScriptedGateway((_, _, _) => { entered.TrySetResult(); return response.Task; });
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(bus, Service(gateway, opts), NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);
        using var cts = new CancellationTokenSource();
        var handling = handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(21), cts.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => handling.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.AreEqual(1, gateway.Calls);
            await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
            await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
        }
        finally
        {
            response.TrySetResult(true);
            await response.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task CancellationDuringBackoffDoesNotPublishOrRetry()
    {
        var opts = Monitor(new PaymentOptions { MaxAttempts = 3, AttemptTimeoutMilliseconds = 1000, InitialBackoffMilliseconds = 1000 });
        var bus = Substitute.For<IEventBus>();
        var timeoutLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CallbackLogger(() => timeoutLogged.TrySetResult());
        var gateway = new ScriptedGateway((_, _, _) => Task.FromException<bool>(new TimeoutException()));
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(bus, new PaymentService(gateway, opts, TimeProvider.System, logger), NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);
        using var cts = new CancellationTokenSource();
        var handling = handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(22), cts.Token);
        await timeoutLogged.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => handling.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(1, gateway.Calls);
        await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentSucceededIntegrationEvent>());
        await bus.DidNotReceive().PublishAsync(Arg.Any<OrderPaymentFailedIntegrationEvent>());
    }

    [TestMethod]
    public async Task EveryGatewayAttemptLogsCorrelatedLatency()
    {
        await AssertLatencyLog(true, _ => Task.FromResult(true), PaymentResult.Succeeded, "Succeeded");
        await AssertLatencyLog(false, _ => Task.FromResult(false), PaymentResult.Declined, "Declined");
        await AssertLatencyLog(true, _ => Task.FromException<bool>(new TimeoutException()), PaymentResult.Unknown, "Timeout");
    }

    private static async Task AssertLatencyLog(bool paymentSucceeds, Func<CancellationToken, Task<bool>> response, PaymentResult expected, string outcome)
    {
        const int orderId = 17;
        var logger = new RecordingLogger();
        var options = Monitor(new PaymentOptions { PaymentSucceeded = paymentSucceeds, MaxAttempts = 1, AttemptTimeoutMilliseconds = 100, InitialBackoffMilliseconds = 1 });
        var result = await new PaymentService(new ScriptedGateway((_, _, ct) => response(ct)), options, TimeProvider.System, logger).ChargeAsync(orderId);
        Assert.AreEqual(expected, result);
        var record = logger.Records.Single(r => r.Level == LogLevel.Information || (outcome == "Timeout" && r.Level == LogLevel.Warning && r.Message.Contains("Gateway attempt timed out")));
        Assert.IsTrue(record.Fields.ContainsKey("ElapsedMilliseconds"));
        Assert.AreEqual(orderId, record.Fields["OrderId"]);
        Assert.AreEqual($"order-payment:{orderId}", record.Fields["IdempotencyKey"]);
        Assert.AreEqual(1, record.Fields["Attempt"]);
        Assert.IsInstanceOfType(record.Fields["ElapsedMilliseconds"], typeof(double));
        Assert.AreEqual(outcome, record.Fields["Outcome"]);
    }

    private sealed class RecordingLogger : ILogger<PaymentService>
    {
        public List<(LogLevel Level, string Message, Dictionary<string, object?> Fields)> Records { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(p => p.Key, p => p.Value) : new Dictionary<string, object?>();
            Records.Add((logLevel, formatter(state, exception), fields));
        }
    }

    private sealed class CallbackLogger(Action callback) : ILogger<PaymentService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("Gateway attempt timed out", StringComparison.Ordinal)) callback();
        }
    }

    private static PaymentService Service(IPaymentGateway gateway, IOptionsMonitor<PaymentOptions> options) => new(gateway, options, TimeProvider.System, NullLogger<PaymentService>.Instance);
    private static IOptionsMonitor<PaymentOptions> Monitor(PaymentOptions value) { var result = Substitute.For<IOptionsMonitor<PaymentOptions>>(); result.CurrentValue.Returns(value); return result; }
    private sealed class ScriptedGateway(params Func<int, string, CancellationToken, Task<bool>>[] scripts) : IPaymentGateway
    {
        public int Calls { get; private set; }
        public List<string> Keys { get; } = [];
        public List<TimeSpan> StartTimes { get; } = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public Task<bool> ChargeAsync(int orderId, string idempotencyKey, CancellationToken cancellationToken) { Keys.Add(idempotencyKey); StartTimes.Add(_clock.Elapsed); var index = Calls++; return scripts[Math.Min(index, scripts.Length - 1)](orderId, idempotencyKey, cancellationToken); }
    }
}
