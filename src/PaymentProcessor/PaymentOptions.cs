namespace eShop.PaymentProcessor;

public class PaymentOptions
{
    public bool PaymentSucceeded { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(2);
    public int TimeoutCount { get; set; }
    public bool TimeoutAfterCharge { get; set; }
}
