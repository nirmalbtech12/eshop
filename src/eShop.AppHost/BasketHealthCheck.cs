using System.Net;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace eShop.AppHost;

// Basket exposes gRPC on an HTTP/2-only endpoint. The standard HTTP probe uses
// HTTP/1.1, which that endpoint rejects before it reaches /health.
internal sealed class BasketHealthCheck(EndpointReference endpoint) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.Url}/health")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy($"HTTP {(int)response.StatusCode}");
        }
        catch (HttpRequestException exception)
        {
            return HealthCheckResult.Unhealthy(exception: exception);
        }
    }
}
