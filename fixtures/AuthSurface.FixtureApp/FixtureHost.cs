using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuthSurface.FixtureApp;

/// <summary>Builds real ASP.NET Core hosts used by integration tests.</summary>
public static class FixtureHost
{
    /// <summary>Builds a WebApplication host with runtime endpoint metadata.</summary>
    /// <param name="fallbackPolicy">Whether an application fallback policy is configured.</param>
    /// <returns>An unstarted WebApplication.</returns>
    public static WebApplication BuildWebApplication(bool fallbackPolicy)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FixtureHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Development,
        });
        ConfigureServices(builder.Services, fallbackPolicy);
        WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        ConfigureEndpoints(app);
        return app;
    }

    /// <summary>Builds a generic-host Startup application with the same endpoint surface.</summary>
    /// <param name="fallbackPolicy">Whether an application fallback policy is configured.</param>
    /// <returns>An unstarted generic host.</returns>
    public static IHost BuildGenericHost(bool fallbackPolicy)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<FixtureStartup>();
                webBuilder.UseSetting(WebHostDefaults.ApplicationKey, typeof(FixtureHost).Assembly.GetName().Name!);
                webBuilder.UseUrls("http://127.0.0.1:0");
                webBuilder.UseSetting("fixture:fallback", fallbackPolicy ? "true" : "false");
            })
            .Build();
    }

    internal static void ConfigureServices(IServiceCollection services, bool fallbackPolicy)
    {
        services.AddControllers().AddApplicationPart(typeof(FixtureController).Assembly);
        services.AddRouting();
        services.AddAuthentication("Bearer").AddScheme<AuthenticationSchemeOptions, NoopAuthenticationHandler>("Bearer", null);
        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder("Bearer")
                .RequireAuthenticatedUser()
                .Build();
            options.FallbackPolicy = fallbackPolicy
                ? new AuthorizationPolicyBuilder("Bearer").RequireAuthenticatedUser().Build()
                : null;
            options.AddPolicy("Named", policy => policy
                .AddAuthenticationSchemes("Bearer")
                .RequireRole("Admin"));
        });
        services.AddSingleton<IAuthorizationPolicyProvider, FixturePolicyProvider>();
    }

    internal static void ConfigureEndpoints(WebApplication app)
    {
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/explicit", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/named", () => Results.Ok()).RequireAuthorization("Named");
        app.MapGet("/dynamic", () => Results.Ok()).RequireAuthorization("Dynamic");
        app.MapGet("/direct-policy", () => Results.Ok())
            .WithMetadata(new AuthorizationPolicyBuilder("Bearer").RequireClaim("scope", "read").Build());
        app.MapGet("/anonymous", () => Results.Ok()).AllowAnonymous();
        app.MapGet("/unprotected", () => Results.Ok());
        app.MapMethods("/multi", ["post", "get"], () => Results.Ok()).RequireAuthorization();
        RouteGroupBuilder group = app.MapGroup("/group").RequireAuthorization("Named");
        group.MapGet("/item", () => Results.Ok());
    }

    internal sealed class FixtureStartup
    {
        private readonly IConfiguration configuration;

        public FixtureStartup(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            bool fallback = bool.TryParse(configuration["fixture:fallback"], out bool value) && value;
            FixtureHost.ConfigureServices(services, fallback);
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapGet("/explicit", () => Results.Ok()).RequireAuthorization();
                endpoints.MapGet("/named", () => Results.Ok()).RequireAuthorization("Named");
                endpoints.MapGet("/dynamic", () => Results.Ok()).RequireAuthorization("Dynamic");
                endpoints.MapGet("/direct-policy", () => Results.Ok())
                    .WithMetadata(new AuthorizationPolicyBuilder("Bearer").RequireClaim("scope", "read").Build());
                endpoints.MapGet("/anonymous", () => Results.Ok()).AllowAnonymous();
                endpoints.MapGet("/unprotected", () => Results.Ok());
                endpoints.MapMethods("/multi", ["post", "get"], () => Results.Ok()).RequireAuthorization();
                endpoints.MapGroup("/group").RequireAuthorization("Named").MapGet("/item", () => Results.Ok());
            });
        }
    }

    private sealed class NoopAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public NoopAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class FixturePolicyProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider inner;

        public FixturePolicyProvider(IOptions<AuthorizationOptions> options)
        {
            inner = new DefaultAuthorizationPolicyProvider(options);
        }

        public bool AllowsCachingPolicies => inner.AllowsCachingPolicies;

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => inner.GetDefaultPolicyAsync();

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => inner.GetFallbackPolicyAsync();

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            policyName == "Dynamic"
                ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder("Bearer")
                    .AddRequirements(new OpaqueRequirement())
                    .Build())
                : inner.GetPolicyAsync(policyName);
    }

    private sealed class OpaqueRequirement : IAuthorizationRequirement
    {
    }
}
