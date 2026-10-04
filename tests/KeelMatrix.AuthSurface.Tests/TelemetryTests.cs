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
    public async Task AssertPolicyCompliantRecordsOneActivationOnSuccess()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        RecordingTelemetry telemetry = new();
        using (AuthSurfaceTelemetryCoordinator.OverrideForTests(telemetry))
        {
            AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
            report.AssertPolicyCompliant();
        }

        Assert.Equal(1, telemetry.ActivationCalls);
    }

    [Fact]
    public async Task AssertPolicyCompliantRecordsOneActivationOnFailure()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: false);
        await app.StartAsync();
        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
        RecordingTelemetry telemetry = new();

        using (AuthSurfaceTelemetryCoordinator.OverrideForTests(telemetry))
        {
            Assert.Throws<AuthSurfaceVerificationException>(report.AssertPolicyCompliant);
        }

        Assert.Equal(1, telemetry.ActivationCalls);
    }

    [Fact]
    public async Task ExplicitBaselineCreationRecordsOneActivation()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
        RecordingTelemetry telemetry = new();

        using (AuthSurfaceTelemetryCoordinator.OverrideForTests(telemetry))
        {
            AuthSurfaceBaseline.Create(report);
        }

        Assert.Equal(1, telemetry.ActivationCalls);
    }

    [Fact]
    public async Task BaselineComparisonRecordsOneActivation()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(report);
        RecordingTelemetry telemetry = new();

        using (AuthSurfaceTelemetryCoordinator.OverrideForTests(telemetry))
        {
            Assert.True(AuthSurfaceVerifier.Compare(report, baseline).IsValid);
        }

        Assert.Equal(1, telemetry.ActivationCalls);
    }

    [Fact]
    public async Task ScanAloneAndZeroEndpointEvaluationDoNotRecordActivation()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        RecordingTelemetry telemetry = new();
        using (AuthSurfaceTelemetryCoordinator.OverrideForTests(telemetry))
        {
            _ = await new AuthSurfaceScanner(app.Services).ScanAsync();
            AuthSurfaceReport report = new([], []);
            Assert.True(AuthSurfaceVerifier.VerifyPolicy(report).IsValid);
        }

        Assert.Equal(0, telemetry.ActivationCalls);
    }

    private sealed class RecordingTelemetry : IAuthSurfaceTelemetry
    {
        public int ActivationCalls { get; private set; }

        public void RecordActivation() => ActivationCalls++;
    }
}
