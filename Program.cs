using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var configuration = builder.Configuration;

var httpClient = new HttpMessageInvoker(new SocketsHttpHandler()
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
    ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
});

var botId = configuration.GetValue<string>("Bot:MicrosoftAppId");
var botEndpoint = configuration.GetValue<string>("Bot:Endpoint");
IEnumerable<string> allowedTenants = configuration.GetSection("Bot:AllowedTenants").Get<string[]>() ?? Enumerable.Empty<string>();

Console.WriteLine($"BotId: {botId}\nBotEndpoint: {botEndpoint}");
Console.WriteLine($"Tenants: {string.Join(",", allowedTenants)}");

services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme,
        options =>
        {
            options.MetadataAddress = "https://login.botframework.com/v1/.well-known/openidconfiguration";
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateActor = false,
                ValidateLifetime = false,
                ValidAudiences = new[]
                {
                    botId,
                },
                ValidateIssuer = false,
                ValidateTokenReplay = false,
                ValidateIssuerSigningKey = false
            };
        });
services.AddReverseProxy();

var app = builder.Build();

// Setup our own request transform class
var requestOptions = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };

app.UseRouting();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.UseEndpoints(endpoints =>
{
    endpoints.Map("/{**catch-all}", async httpContext =>
    {
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var logger = app.Services.GetRequiredService<ILogger<Program>>();

        var tenantHeader = httpContext.Request.Headers["X-Ms-Tenant-Id"].FirstOrDefault();
        var isAllowedTenant = allowedTenants.Contains(tenantHeader);
        if (!isAllowedTenant)
        {
            logger.LogWarning("Invalid tenant: {tenantId}", tenantHeader);
            httpContext.Response.StatusCode = 403;
            return;
        }
        logger.LogInformation("Request for tenant: {tenantId} {isAllowedTenant}", tenantHeader, isAllowedTenant);
        logger.LogInformation("Allowed tenants: {tenants}", string.Join(", ", allowedTenants));

        var urlBase = configuration.GetValue<string>("Bot:Endpoint");
        var error = await forwarder.SendAsync(httpContext, urlBase, httpClient, requestOptions);

        // Check if the proxy operation was successful
        if (error != ForwarderError.None)
        {
            var errorFeature = httpContext.Features.Get<IForwarderErrorFeature>();
            var exception = errorFeature?.Exception;
        }
    }).RequireAuthorization();
});

app.Run();
