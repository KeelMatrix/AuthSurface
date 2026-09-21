using System.Runtime.CompilerServices;

namespace KeelMatrix.AuthSurface.Tests;

internal static class TelemetryTestSetup
{
    [ModuleInitializer]
    internal static void DisableTelemetry()
    {
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        Environment.SetEnvironmentVariable("DO_NOT_TRACK", "1");
    }
}
