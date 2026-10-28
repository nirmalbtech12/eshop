namespace eShop.PaymentProcessor;

public class PaymentOptions
{
    public bool PaymentSucceeded { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int AttemptTimeoutMilliseconds { get; set; } = 5000;
    public int InitialBackoffMilliseconds { get; set; } = 250;
}

