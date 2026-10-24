using Npgsql;

namespace eShop.PaymentProcessor;

public sealed class PostgresPaymentChargeStore(NpgsqlDataSource dataSource) : IPaymentChargeStore
{
    public async Task<bool> GetOrCreateChargeAsync(int orderId, string idempotencyKey, bool paymentSucceeded)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var createTable = connection.CreateCommand())
        {
            createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS payment_gateway_charges (
                order_id INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL,
                succeeded BOOLEAN NOT NULL,
                PRIMARY KEY (order_id, idempotency_key)
            );
            """;
            await createTable.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO payment_gateway_charges (idempotency_key, order_id, succeeded)
            VALUES (@key, @orderId, @succeeded)
            ON CONFLICT (order_id, idempotency_key)
            DO UPDATE SET idempotency_key = EXCLUDED.idempotency_key
            RETURNING succeeded;
            """;
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("succeeded", paymentSucceeded);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
