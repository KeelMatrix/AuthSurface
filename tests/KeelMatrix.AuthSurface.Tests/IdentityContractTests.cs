using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

#pragma warning disable ASP0022
public sealed class IdentityContractTests
{
    [Fact]
    public async Task RealHostLiteralCasingDuplicateFailsWithStructuredIdentityError()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/Case", () => Results.Ok()).AllowAnonymous();
            application.MapGet("/case", () => Results.Ok()).AllowAnonymous();
        });
        await app.StartAsync();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(app.Services).ScanAsync());

        Assert.Equal("duplicate-endpoint-identity", exception.Code);
    }

    [Fact]
    public async Task RealHostParameterNameCasingDuplicateFailsWithStructuredIdentityError()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/items/{id}", () => Results.Ok()).AllowAnonymous();
            application.MapGet("/items/{ID}", () => Results.Ok()).AllowAnonymous();
        });
        await app.StartAsync();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(app.Services).ScanAsync());

        Assert.Equal("duplicate-endpoint-identity", exception.Code);
    }

    [Fact]
    public async Task RealHostGenuinelyDifferentRoutesRemainDistinct()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/alpha", () => Results.Ok()).AllowAnonymous();
            application.MapGet("/beta", () => Results.Ok()).AllowAnonymous();
        });
        await app.StartAsync();

        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();

        Assert.Contains(report.Endpoints, endpoint => endpoint.Route == "/alpha");
        Assert.Contains(report.Endpoints, endpoint => endpoint.Route == "/beta");
    }

    private static WebApplication BuildApplication(Action<WebApplication> configureEndpoints)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FixtureHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddAuthorization();
        WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        configureEndpoints(app);
        return app;
    }
}
#pragma warning restore ASP0022
