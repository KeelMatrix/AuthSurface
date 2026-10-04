using System.Threading;
using KeelMatrix.Telemetry;

namespace KeelMatrix.AuthSurface;

internal interface IAuthSurfaceTelemetry
{
    void RecordActivation();
}

internal static class AuthSurfaceTelemetryCoordinator
{
    private static readonly AsyncLocal<IAuthSurfaceTelemetry?> TestOverride = new();
    private static readonly IAuthSurfaceTelemetry Shared = new AuthSurfaceTelemetry();

    internal static void RecordActivationForEvaluation(int endpointCount)
    {
        if (endpointCount > 0)
        {
            (TestOverride.Value ?? Shared).RecordActivation();
        }
    }

    internal static IDisposable OverrideForTests(IAuthSurfaceTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        IAuthSurfaceTelemetry? previous = TestOverride.Value;
        TestOverride.Value = telemetry;
        return new RestoreOverride(previous);
    }

    private sealed class RestoreOverride : IDisposable
    {
        private readonly IAuthSurfaceTelemetry? previous;

        public RestoreOverride(IAuthSurfaceTelemetry? previous) => this.previous = previous;

        public void Dispose() => TestOverride.Value = previous;
    }
}

internal sealed class AuthSurfaceTelemetry : IAuthSurfaceTelemetry
{
    private readonly Client client = new("authsurface", typeof(AuthSurfaceScanner));

    public void RecordActivation() => client.TrackActivation();
}
