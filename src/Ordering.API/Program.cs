using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddApplicationServices();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<OrderingDatabaseHealthCheck>("orderingdb");

var withApiVersioning = builder.Services.AddApiVersioning(options =>
{
    // Include "api-supported-versions" and "api-deprecated-versions" headers in all responses
    options.ReportApiVersions = true;
});

builder.AddDefaultOpenApi(withApiVersioning);

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseRouting();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/health") &&
        !context.Request.Path.StartsWithSegments("/alive"))
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var isHealthy = false;
        try
        {
            var health = await context.RequestServices.GetRequiredService<HealthCheckService>().CheckHealthAsync(timeout.Token);
            isHealthy = health.Status == HealthStatus.Healthy;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            // A readiness timeout means the service is unavailable, not that the request succeeded.
        }

        if (!isHealthy)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Ordering service is not ready.");
            return;
        }
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

var orders = app.NewVersionedApi("Orders");

orders.MapOrdersApiV1()
      .RequireAuthorization();

app.UseDefaultOpenApi();
app.Run();

public sealed class OrderingDatabaseHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString("orderingdb"))
        {
            Pooling = false,
            Timeout = 2
        }.ConnectionString;
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
