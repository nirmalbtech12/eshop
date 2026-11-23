extern alias payment;
using System.Threading;
using payment::eShop.PaymentProcessor;
using payment::eShop.PaymentProcessor.Gateway;
using Npgsql;
using payment::eShop.PaymentProcessor.Payments;
using payment::eShop.PaymentProcessor.IntegrationEvents.EventHandling;
using payment::eShop.PaymentProcessor.IntegrationEvents.Events;
using eShop.EventBus.Abstractions;
using eShop.EventBus.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace eShop.Ordering.FunctionalTests;

public class PaymentGatewayTests(PaymentGatewayFixture fixture) : IClassFixture<PaymentGatewayFixture>
{
    [Fact]
    public async Task DurableGatewayProbe()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Guid paymentId;
        await using (var source = NpgsqlDataSource.Create(fixture.ConnectionString))
        {
            var gateway = new SimulatedPaymentGateway(source, PaymentGatewayFixture.OptionsMonitor(new PaymentOptions()));
            paymentId = (await gateway.PayAsync(200006, "order-200006", deadline.Token)).PaymentId;
        }
        await using var reopened = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var command = reopened.CreateCommand("SELECT idempotency_key, payment_id, succeeded FROM payment_results WHERE order_id = 200006");
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.Equal("order-200006", reader.GetString(0));
        Assert.Equal(paymentId, reader.GetGuid(1));
        Assert.True(reader.GetBoolean(2));
        Assert.False(await reader.ReadAsync(deadline.Token));
    }    [Fact]
    public async Task CommittedChargeIsReusedAfterLostResponse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        const int order = 200001;
        const string key = "order-200001";
        Guid original;
        await using (var source = NpgsqlDataSource.Create(fixture.ConnectionString))
        {
            var gateway = new SimulatedPaymentGateway(source, PaymentGatewayFixture.OptionsMonitor(new PaymentOptions { TimeoutCount = 1, TimeoutAfterCharge = true }));
            using var lost = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.PayAsync(order, key, lost.Token));
            await using var command = source.CreateCommand("SELECT payment_id FROM payment_results WHERE order_id = 200001 AND succeeded");
            original = (Guid)(await command.ExecuteScalarAsync(timeout.Token));
        }
        await using var reopened = NpgsqlDataSource.Create(fixture.ConnectionString);
        var recovered = new SimulatedPaymentGateway(reopened, PaymentGatewayFixture.OptionsMonitor(new PaymentOptions()));
        var result = await recovered.PayAsync(order, key, timeout.Token);
        Assert.Equal(original, result.PaymentId);
        Assert.True(result.Succeeded);
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new SimulatedPaymentGateway(reopened, PaymentGatewayFixture.OptionsMonitor(new PaymentOptions())).PayAsync(order, key, timeout.Token)));
        Assert.All(duplicates, item => Assert.Equal(original, item.PaymentId));
        var other = await recovered.PayAsync(200002, "order-200002", timeout.Token);
        Assert.NotEqual(original, other.PaymentId);
        await using var count = reopened.CreateCommand("SELECT count(*) FROM payment_results WHERE order_id = 200001 AND succeeded");
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(timeout.Token)));
    }
    [Fact]
    public async Task ConcurrentNewOrderHasOneChargeAndDeclineIsNotACharge()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        const int order = 200003;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
            var gateway = new SimulatedPaymentGateway(source, PaymentGatewayFixture.OptionsMonitor(new PaymentOptions()));
            await start.Task;
            return await gateway.PayAsync(order, "order-200003", timeout.Token);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(timeout.Token);
        Assert.All(results, result => Assert.Equal(results[0].PaymentId, result.PaymentId));
        await using var reopened = NpgsqlDataSource.Create(fixture.ConnectionString);
        var decline = new SimulatedPaymentGateway(reopened, PaymentGatewayFixture.OptionsMonitor(new PaymentOptions { PaymentSucceeded = false }));
        Assert.False((await decline.PayAsync(200004, "order-200004", timeout.Token)).Succeeded);
        await using var query = reopened.CreateCommand("SELECT order_id, idempotency_key, payment_id, succeeded FROM payment_results WHERE order_id IN (200003, 200004) ORDER BY order_id");
        await using var reader = await query.ExecuteReaderAsync(timeout.Token);
        Assert.True(await reader.ReadAsync(timeout.Token));
        Assert.Equal(200003, reader.GetInt32(0));
        Assert.Equal("order-200003", reader.GetString(1));
        Assert.Equal(results[0].PaymentId, reader.GetGuid(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(await reader.ReadAsync(timeout.Token));
        Assert.Equal(200004, reader.GetInt32(0));
        Assert.Equal("order-200004", reader.GetString(1));
        Assert.NotEqual(results[0].PaymentId, reader.GetGuid(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(await reader.ReadAsync(timeout.Token));
    }    [Fact]
    public async Task ProductionGatewayTimeoutRecoversThroughHandler()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
        var settings = PaymentGatewayFixture.OptionsMonitor(new PaymentOptions
        {
            TimeoutCount = 2, TimeoutAfterCharge = true,
            AttemptTimeout = TimeSpan.FromMilliseconds(200), InitialBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(40)
        });
        var gateway = new SimulatedPaymentGateway(source, settings);
        var service = new PaymentService(gateway, settings, TimeProvider.System, NullLogger<PaymentService>.Instance);
        var bus = new RecordingBus();
        var handler = new OrderStatusChangedToStockConfirmedIntegrationEventHandler(bus, service,
            new TestLifetime(), NullLogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler>.Instance);
        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(200005)).WaitAsync(deadline.Token);
        var published = Assert.Single(bus.Events);
        Assert.Equal(200005, Assert.IsType<OrderPaymentSucceededIntegrationEvent>(published).OrderId);
        await handler.Handle(new OrderStatusChangedToStockConfirmedIntegrationEvent(200005)).WaitAsync(deadline.Token);
        Assert.Equal(2, bus.Events.Count);
        Assert.All(bus.Events, item => Assert.Equal(200005, Assert.IsType<OrderPaymentSucceededIntegrationEvent>(item).OrderId));
        await using var read = source.CreateCommand("SELECT idempotency_key, succeeded, count(*) OVER () FROM payment_results WHERE order_id = 200005");
        await using var reader = await read.ExecuteReaderAsync(deadline.Token);
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.Equal("order-200005", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }
    private sealed class RecordingBus : IEventBus
    {
        public List<IntegrationEvent> Events { get; } = [];
        public Task PublishAsync(IntegrationEvent item) { Events.Add(item); return Task.CompletedTask; }
    }
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }}
