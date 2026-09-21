using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task TelemetryOverrideReceivesNoProductOrSecurityPayload()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        RecordingTelemetry telemetry = new();
        using (AuthSurfaceTelemetryCoordinator.OverrideForTests(telemetry))
        {
            AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
            AuthSurfaceVerificationResult result = AuthSurfaceVerifier.VerifyPolicy(report);
            Assert.True(result.IsValid);
        }

        Assert.Equal(1, telemetry.ActivationCalls);
        Assert.Empty(typeof(IAuthSurfaceTelemetry).GetMethods().Single().GetParameters());
    }

    [Fact]
    public async Task ThrowingTelemetryCannotChangeVerificationOutcome()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
        using (AuthSurfaceTelemetryCoordinator.OverrideForTests(new ThrowingTelemetry()))
        {
            AuthSurfaceVerificationResult result = AuthSurfaceVerifier.VerifyPolicy(report);
            Assert.True(result.IsValid);
        }
    }

    private sealed class RecordingTelemetry : IAuthSurfaceTelemetry
    {
        public int ActivationCalls { get; private set; }

        public void RecordActivation() => ActivationCalls++;
    }

    private sealed class ThrowingTelemetry : IAuthSurfaceTelemetry
    {
        public void RecordActivation() => throw new InvalidOperationException("telemetry test failure");
    }
}
