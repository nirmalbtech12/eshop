using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;

[assembly: HostingStartup(typeof(eShop.Ordering.FunctionalTests.ProcessHostingStartup))]
namespace eShop.Ordering.FunctionalTests;

// Loaded only by the ordering process owned by CheckoutRestartTests.
public sealed class ProcessHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
    {
        services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, ProcessTestAuthentication>("Test", _ => { });
        services.AddSingleton<IStartupFilter, ProcessTestFilter>();
    });
    }
}

internal sealed class ProcessTestFilter(IConfiguration configuration) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<AutoAuthorizeMiddleware>();
        app.Use(async (context, following) =>
        {
            await following();
            if (context.Request.Method == "POST" && context.Response.StatusCode == StatusCodes.Status200OK &&
                context.Request.Headers["x-requestid"] == configuration["TestPostCommitRequest"])
                context.Abort();
        });
        next(app);
    };
}

internal sealed class ProcessTestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", AutoAuthorizeMiddleware.IDENTITY_ID),
            new Claim(ClaimTypes.Name, AutoAuthorizeMiddleware.IDENTITY_ID)], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}
