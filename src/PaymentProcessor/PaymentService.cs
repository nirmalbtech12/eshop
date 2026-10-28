namespace eShop.PaymentProcessor;

public enum PaymentResult { Succeeded, Declined, Unknown }

public sealed class PaymentService(
    IPaymentGateway gateway,
    IOptionsMonitor<PaymentOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentService> logger)
{
    public async Task<PaymentResult> ChargeAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;
        var key = $"order-payment:{orderId}";
        for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var started = timeProvider.GetTimestamp();
            Task<bool> charge = gateway.ChargeAsync(orderId, key, timeout.Token);
            var deadline = Task.Delay(TimeSpan.FromMilliseconds(settings.AttemptTimeoutMilliseconds), timeProvider, cancellationToken);
            var completed = await Task.WhenAny(charge, deadline);
            var elapsed = timeProvider.GetElapsedTime(started).TotalMilliseconds;
            if (completed == charge)
            {
                timeout.Cancel();
                try
                {
                    var succeeded = await charge;
                    logger.LogInformation("Gateway attempt completed. OrderId={OrderId} IdempotencyKey={IdempotencyKey} Attempt={Attempt} ElapsedMilliseconds={ElapsedMilliseconds} Outcome={Outcome}", orderId, key, attempt, elapsed, succeeded ? "Succeeded" : "Declined");
                    return succeeded ? PaymentResult.Succeeded : PaymentResult.Declined;
                }
                catch (TimeoutException)
                {
                    logger.LogWarning("Gateway attempt timed out. OrderId={OrderId} IdempotencyKey={IdempotencyKey} Attempt={Attempt} ElapsedMilliseconds={ElapsedMilliseconds} Outcome={Outcome}", orderId, key, attempt, elapsed, "Timeout");
                    if (attempt == settings.MaxAttempts) return PaymentResult.Unknown;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, (double)settings.InitialBackoffMilliseconds * Math.Pow(2, attempt - 1))), timeProvider, cancellationToken);
                    continue;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            timeout.Cancel();
            _ = charge.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            logger.LogWarning("Gateway attempt timed out. OrderId={OrderId} IdempotencyKey={IdempotencyKey} Attempt={Attempt} ElapsedMilliseconds={ElapsedMilliseconds} Outcome={Outcome}", orderId, key, attempt, elapsed, "Timeout");
            if (attempt == settings.MaxAttempts)
            {
                logger.LogWarning("Payment outcome is unknown after {AttemptCount} attempts for order {OrderId}.", attempt, orderId);
                return PaymentResult.Unknown;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Min(5000, (double)settings.InitialBackoffMilliseconds * Math.Pow(2, attempt - 1)));
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
        return PaymentResult.Unknown;
    }
}
