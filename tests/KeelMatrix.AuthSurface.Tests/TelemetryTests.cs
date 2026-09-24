using System.Diagnostics;
using System.Text.Json;
using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using TelemetryProbeProgram = KeelMatrix.AuthSurface.TelemetryProbe.Program;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class TelemetryTests
{
    private static readonly HashSet<string> AllowedPayloadFields =
    [
        "event",
        "tool",
        "tool_version",
        "telemetry_version",
        "schema_version",
        "project_hash",
        "installation_hash",
        "runtime",
        "os",
        "ci",
        "timestamp",
    ];

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

    [Fact]
    public async Task FreshProcessActualPayloadContainsOnlySharedAllowedFields()
    {
        ProcessResult result = await RunProbeAsync("capture");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("RESULT=VALID;ENDPOINTS=", result.StandardOutput, StringComparison.Ordinal);
        string payload = Assert.Single(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => line.StartsWith("PAYLOAD=", StringComparison.Ordinal))["PAYLOAD=".Length..];
        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.NotEmpty(document.RootElement.EnumerateObject());
        Assert.All(document.RootElement.EnumerateObject(), property => Assert.Contains(property.Name, AllowedPayloadFields));
        Assert.Contains("project_hash", document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Contains("installation_hash", document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.DoesNotContain("/explicit", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Named", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Admin", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("authsurface.json", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("FixtureController", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshProcessEnvironmentOptOutSuppressesPayload()
    {
        ProcessResult result = await RunProbeAsync("suppressed:KEELMATRIX_NO_TELEMETRY");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("NO_PAYLOAD", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("RESULT=VALID;ENDPOINTS=", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DOTNET_CLI_TELEMETRY_OPTOUT")]
    [InlineData("DO_NOT_TRACK")]
    public async Task FreshProcessEachDocumentedEnvironmentOptOutSuppressesPayload(string variable)
    {
        ProcessResult result = await RunProbeAsync("suppressed:" + variable);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("NO_PAYLOAD", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("RESULT=VALID;ENDPOINTS=", result.StandardOutput, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunProbeAsync(string mode)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(TelemetryProbeProgram).Assembly.Location);
        startInfo.ArgumentList.Add(mode);
        startInfo.Environment.Remove("KEELMATRIX_NO_TELEMETRY");
        startInfo.Environment.Remove("DOTNET_CLI_TELEMETRY_OPTOUT");
        startInfo.Environment.Remove("DO_NOT_TRACK");

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Telemetry probe could not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeelMatrix.AuthSurface.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("AuthSurface repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

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
