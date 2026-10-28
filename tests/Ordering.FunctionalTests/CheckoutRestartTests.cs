extern alias webapp;

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Ordering.API.Application.Commands;
using eShop.Ordering.API.Application.Models;
using eShop.Ordering.Infrastructure;
using eShop.Ordering.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace eShop.Ordering.FunctionalTests;

public sealed class CheckoutRestartTests(OrderingApiFixture fixture) : IClassFixture<OrderingApiFixture>
{
    [Fact]
    public async Task CheckoutSurvivesOrderingRestart()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var preId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var preUser = Guid.NewGuid().ToString();
        var postUser = Guid.NewGuid().ToString();
        var item = new BasketItem { Id = Guid.NewGuid().ToString(), ProductId = 12, ProductName = "Restart test", UnitPrice = 10, Quantity = 1 };
        CreateOrderRequest Order(string user) => new(user, user, "city", "street", "state", "country", "zip", "1111222233334444", "Test", DateTime.UtcNow.AddYears(1), "111", 1, user, [item]);
        var preOrder = Order(preUser);
        var postOrder = Order(postUser);
        var options = new DbContextOptionsBuilder<OrderingContext>().UseNpgsql(fixture.OrderingConnectionString).Options;

        async Task VerifyAsync(Guid id, string user, bool marker, int orders)
        {
            await using var db = new OrderingContext(options);
            Assert.Equal(marker, await db.Set<ClientRequest>().AnyAsync(r => r.Id == id, token));
            Assert.Equal(orders, await db.Orders.CountAsync(o => o.Buyer.IdentityGuid == user, token));
        }
        async Task SendAsync(HttpClient client, CreateOrderRequest order, Guid id)
        {
            var webOrder = JsonSerializer.Deserialize<webapp::eShop.WebApp.Services.CreateOrderRequest>(JsonSerializer.Serialize(order))!;
            await new webapp::eShop.WebApp.Services.OrderingService(client).CreateOrder(webOrder, id);
        }

        // A test-owned AFTER INSERT trigger holds an advisory lock after the
        // marker row is inserted. Terminate the actual process at that boundary.
        var trigger = "checkout_" + preId.ToString("N");
        var key = BitConverter.ToInt64(preId.ToByteArray(), 0) & long.MaxValue;
        int firstProcessId;
        await using (var first = await OrderingProcess.StartAsync(fixture, null, token))
        {
            firstProcessId = first.ProcessId;
            await using var db = new OrderingContext(options);
            await db.Database.ExecuteSqlRawAsync($"CREATE FUNCTION ordering.\"{trigger}\"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"Id\" = '{preId}'::uuid THEN PERFORM pg_advisory_xact_lock({key}); PERFORM pg_sleep(5); END IF; RETURN NEW; END; $$; CREATE TRIGGER \"{trigger}\" AFTER INSERT ON ordering.requests FOR EACH ROW EXECUTE FUNCTION ordering.\"{trigger}\"();", token);
            try
            {
                using var client = first.CreateClient();
                var request = SendAsync(client, preOrder, preId);
                await using var observer = new NpgsqlConnection(fixture.OrderingConnectionString);
                await observer.OpenAsync(token);
                while (true)
                {
                    await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", observer);
                    acquire.Parameters.AddWithValue("key", key);
                    if (!(bool)(await acquire.ExecuteScalarAsync(token))!) break;
                    await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", observer);
                    release.Parameters.AddWithValue("key", key);
                    await release.ExecuteScalarAsync(token);
                    Assert.False(request.IsCompleted, "Checkout ended before the request marker was inserted.");
                    await Task.Delay(100, token);
                }
                await first.StopAsync(token);
                Assert.NotNull(await Record.ExceptionAsync(() => request));
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS \"{trigger}\" ON ordering.requests; DROP FUNCTION IF EXISTS ordering.\"{trigger}\"();", CancellationToken.None);
            }
        }
        int secondProcessId;
        await using (var second = await OrderingProcess.StartAsync(fixture, postId, token))
        {
            secondProcessId = second.ProcessId;
            Assert.NotEqual(firstProcessId, secondProcessId);
            using var client = second.CreateClient();
            await VerifyAsync(preId, preUser, false, 0);
            await SendAsync(client, preOrder, preId);
            await VerifyAsync(preId, preUser, true, 1);
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => SendAsync(client, postOrder, postId));
            await VerifyAsync(postId, postUser, true, 1);
            await second.StopAsync(token);
        }
        await using (var third = await OrderingProcess.StartAsync(fixture, null, token))
        {
            Assert.NotEqual(secondProcessId, third.ProcessId);
            using var client = third.CreateClient();
            await VerifyAsync(preId, preUser, true, 1);
            await VerifyAsync(postId, postUser, true, 1);
            await SendAsync(client, postOrder, postId);
            await VerifyAsync(postId, postUser, true, 1);
            await third.StopAsync(token);
        }
    }

    private sealed class OrderingProcess(Process process, Uri address, StringBuilder output) : IAsyncDisposable
    {
        public static async Task<OrderingProcess> StartAsync(OrderingApiFixture fixture, Guid? postId, CancellationToken token)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var address = new Uri($"http://127.0.0.1:{port}");
            var directory = AppContext.BaseDirectory;
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "exec", "--depsfile", Path.Combine(directory, "Ordering.FunctionalTests.deps.json"), "--runtimeconfig", Path.Combine(directory, "Ordering.API.runtimeconfig.json"), Path.Combine(directory, "Ordering.API.dll") }) start.ArgumentList.Add(arg);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = address.ToString();
            start.Environment["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = "Ordering.FunctionalTests";
            start.Environment["ConnectionStrings__orderingdb"] = fixture.OrderingConnectionString;
            start.Environment["ConnectionStrings__eventbus"] = fixture.EventBusConnectionString;
            start.Environment["Identity__Url"] = fixture.IdentityUrl;
            start.Environment["Identity__Audience"] = "orders";
            start.Environment["Identity__Scopes__orders"] = "Ordering API";
            start.Environment["EventBus__SubscriptionClientName"] = "checkout-" + Guid.NewGuid();
            start.Environment["TestPostCommitRequest"] = postId?.ToString() ?? "";
            var output = new StringBuilder();
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) { if (output.Length < 30000) output.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) { if (output.Length < 30000) output.AppendLine(e.Data); } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var host = new OrderingProcess(process, address, output);
            try
            {
                using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(2) };
                while (!token.IsCancellationRequested)
                {
                    if (process.HasExited) throw new InvalidOperationException($"Ordering process exited: {output}");
                    try { using var response = await client.GetAsync("/health", token); if (response.IsSuccessStatusCode) return host; }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                    await Task.Delay(250, token);
                }
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Ordering did not become healthy.");
            }
            catch { await host.DisposeAsync(); throw; }
        }
        public int ProcessId => process.Id;
        public HttpClient CreateClient() => new(new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)) { InnerHandler = new HttpClientHandler() }) { BaseAddress = address };
        public async Task StopAsync(CancellationToken token)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(token);
            Assert.True(process.HasExited, $"Ordering process did not stop: {output}");
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process.Dispose();
        }
    }
}
