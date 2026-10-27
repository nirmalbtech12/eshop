var role = args[0];
var gateFile = args[1];
var port = Environment.GetEnvironmentVariable("PORT")
    ?? throw new InvalidOperationException("PORT was not supplied by Aspire.");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

app.MapGet("/health", () => role == "ordering" && !File.Exists(gateFile)
    ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
    : Results.Ok());

await app.RunAsync();
