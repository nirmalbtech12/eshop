#nullable enable
using eShop.PaymentProcessor.Gateway;

namespace eShop.PaymentProcessor.Payments;

public class PaymentService(IPaymentGateway gateway, IOptionsMonitor<PaymentOptions> options,
    TimeProvider timeProvider, ILogger<PaymentService> logger)
{
    public async Task<PaymentResult?> ProcessAsync(int orderId, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var key = $"order-{orderId}";
        for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
        {
            using var deadline = new CancellationTokenSource(settings.AttemptTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var started = timeProvider.GetTimestamp();
            var outcome = "error";
            try
            {
                var result = await gateway.PayAsync(orderId, key, linked.Token);
                outcome = result.Succeeded ? "success" : "declined";
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                outcome = "timeout";
            }
            catch (TimeoutException)
            {
                outcome = "timeout";
            }
            finally
            {
                logger.LogInformation("Payment gateway attempt {Attempt} for order {OrderId} key {IdempotencyKey} completed with {Outcome} in {ElapsedMilliseconds} ms",
                    attempt, orderId, key, outcome, timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }
            if (attempt < settings.MaxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(settings.MaxBackoff.TotalMilliseconds,
                    settings.InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1)));
                logger.LogInformation("Retrying payment for order {OrderId} after {DelayMilliseconds} ms", orderId, delay.TotalMilliseconds);
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
        logger.LogWarning("Payment retries exhausted for order {OrderId} key {IdempotencyKey}", orderId, key);
        return null;
    }
}

