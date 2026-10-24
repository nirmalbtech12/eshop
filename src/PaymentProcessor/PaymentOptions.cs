namespace eShop.PaymentProcessor;

public class PaymentOptions
{
    public bool PaymentSucceeded { get; set; }
    public int MaxTimeoutRetries { get; set; } = 2;
    public int RetryBackoffMilliseconds { get; set; } = 100;
}

