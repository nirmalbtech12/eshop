namespace eShop.PaymentProcessor;

public class PaymentOptions
{
    public bool PaymentSucceeded { get; set; }
    public int MaxTimeoutRetries { get; set; } = 2;
    public int RetryDelayMilliseconds { get; set; } = 100;
    public int SimulatedTimeoutAttempts { get; set; }
}

