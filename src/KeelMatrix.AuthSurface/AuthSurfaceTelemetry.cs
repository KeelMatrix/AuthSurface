using System.Threading;
using System.Threading.Channels;
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

    internal static void RecordEvaluation(int endpointCount, int violationCount, bool usedBaseline)
    {
        try
        {
            if (endpointCount > 0)
            {
                (TestOverride.Value ?? Shared).RecordActivation();
            }
        }
        catch
        {
            // Telemetry is best-effort and cannot affect scan or comparison results.
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

internal sealed class AuthSurfaceTelemetry : IAuthSurfaceTelemetry, IDisposable
{
    private readonly Channel<byte> pending = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Client client = new("authsurface", typeof(AuthSurfaceScanner));
    private Task? worker;
    private int started;
    private int disposed;

    public void RecordActivation()
    {
        try
        {
            if (Volatile.Read(ref disposed) != 0 ||
                Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY") is "1" or "true" or "TRUE")
            {
                return;
            }

            if (Interlocked.CompareExchange(ref started, 1, 0) == 0)
            {
                worker = Task.Run(DispatchAsync);
            }

            pending.Writer.TryWrite(0);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        pending.Writer.TryComplete();
        try
        {
            worker?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
    }

    private async Task DispatchAsync()
    {
        try
        {
            await foreach (byte _ in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    client.TrackActivation();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
