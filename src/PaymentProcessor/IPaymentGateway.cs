namespace eShop.PaymentProcessor;

public interface IPaymentGateway
{
    Task<bool> ChargeAsync(int orderId, string idempotencyKey);
}

public interface IPaymentResultStore
{
    Task<PaymentAttemptResult> AttemptChargeAsync(string idempotencyKey, bool result, int timeoutsBeforeResult);
}

public sealed record PaymentAttemptResult(bool TimedOut, bool Succeeded);

public sealed class RedisPaymentResultStore(IConnectionMultiplexer redis) : IPaymentResultStore
{
    private const string AttemptChargeScript = """
        local result = redis.call('GET', KEYS[1])
        if result then return result end
        local attempt = redis.call('INCR', KEYS[2])
        if attempt <= tonumber(ARGV[2]) then return 'timeout' end
        result = ARGV[1]
        redis.call('SET', KEYS[1], result)
        return result
        """;

    public async Task<PaymentAttemptResult> AttemptChargeAsync(string idempotencyKey, bool result, int timeoutsBeforeResult)
    {
        var key = $"{{{idempotencyKey}}}";
        var resultKey = (RedisKey)$"payment:charge:{key}:result";
        var attemptsKey = (RedisKey)$"payment:charge:{key}:attempts";
        var response = await redis.GetDatabase().ScriptEvaluateAsync(
            AttemptChargeScript,
            [resultKey, attemptsKey],
            [result ? "true" : "false", Math.Max(0, timeoutsBeforeResult)]);
        var value = (string)response;
        return value == "timeout"
            ? new PaymentAttemptResult(TimedOut: true, Succeeded: false)
            : new PaymentAttemptResult(TimedOut: false, Succeeded: value == "true");
    }
}

public sealed class SimulatedPaymentChargeProcessor(
    IPaymentResultStore paymentResultStore,
    IOptionsMonitor<PaymentOptions> options) : IPaymentChargeProcessor
{
    // This simulator models timeout attempts and a final result; it does not move funds.
    public async Task<bool> ChargeAsync(int orderId, string idempotencyKey)
    {
        var result = await paymentResultStore.AttemptChargeAsync(
            idempotencyKey,
            options.CurrentValue.PaymentSucceeded,
            options.CurrentValue.SimulatedTimeoutAttempts);
        if (result.TimedOut)
        {
            throw new TimeoutException($"Simulated payment gateway timeout for order {orderId}.");
        }
        return result.Succeeded;
    }
}

public interface IPaymentChargeProcessor
{
    /// <summary>
    /// Executes one payment operation for a key. Implementations backed by a real provider
    /// must pass this key to the provider's durable idempotency facility.
    /// </summary>
    Task<bool> ChargeAsync(int orderId, string idempotencyKey);
}

public sealed class SimulatedPaymentGateway(IPaymentChargeProcessor chargeProcessor) : IPaymentGateway
{
    public Task<bool> ChargeAsync(int orderId, string idempotencyKey) => chargeProcessor.ChargeAsync(orderId, idempotencyKey);
}

public interface IRetryDelay
{
    Task DelayAsync(TimeSpan delay);
}

public sealed class RetryDelay : IRetryDelay
{
    public Task DelayAsync(TimeSpan delay) => Task.Delay(delay);
}
