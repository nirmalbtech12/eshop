using System.Collections.Concurrent;
using Npgsql;

namespace eShop.PaymentProcessor.Gateway;

public class SimulatedPaymentGateway(NpgsqlDataSource dataSource, IOptionsMonitor<PaymentOptions> options) : IPaymentGateway
{
    private readonly ConcurrentDictionary<int, int> attempts = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS payment_results (
                idempotency_key text PRIMARY KEY, order_id integer NOT NULL UNIQUE,
                payment_id uuid NOT NULL UNIQUE, succeeded boolean NOT NULL,
                created_utc timestamptz NOT NULL DEFAULT now())
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PaymentResult> PayAsync(int orderId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var timeout = attempts.AddOrUpdate(orderId, 1, (_, count) => count + 1) <= settings.TimeoutCount;
        if (timeout && !settings.TimeoutAfterCharge)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        await using (var insert = dataSource.CreateCommand("""
            INSERT INTO payment_results (idempotency_key, order_id, payment_id, succeeded)
            VALUES (@key, @order, @payment, @succeeded) ON CONFLICT DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("key", idempotencyKey);
            insert.Parameters.AddWithValue("order", orderId);
            insert.Parameters.AddWithValue("payment", Guid.NewGuid());
            insert.Parameters.AddWithValue("succeeded", settings.PaymentSucceeded);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (timeout && settings.TimeoutAfterCharge)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        await using var read = dataSource.CreateCommand("SELECT payment_id, succeeded FROM payment_results WHERE idempotency_key = @key AND order_id = @order");
        read.Parameters.AddWithValue("key", idempotencyKey);
        read.Parameters.AddWithValue("order", orderId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Payment identity conflicts with an existing order.");
        return new PaymentResult(reader.GetGuid(0), reader.GetBoolean(1));
    }
}
