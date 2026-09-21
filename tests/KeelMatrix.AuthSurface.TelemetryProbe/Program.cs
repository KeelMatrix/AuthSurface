using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using KeelMatrix.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KeelMatrix.AuthSurface.TelemetryProbe;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool suppressed = args.FirstOrDefault() == "suppressed";
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", suppressed ? "1" : null);
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", null);
        Environment.SetEnvironmentVariable("DO_NOT_TRACK", null);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        SetTelemetryUrl(new Uri($"http://127.0.0.1:{port}/"));
        string identityRoot = CreateIdentityRoot();
        SetTelemetryIdentityStartingPoint(identityRoot);

        try
        {
            using CancellationTokenSource captureTimeout = new(TimeSpan.FromSeconds(suppressed ? 2 : 15));
            Task<string?> capture = CaptureAsync(listener, captureTimeout.Token);

            await using WebApplication app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
            await app.StartAsync();
            AuthSurfaceScanner scanner = new(
                app.Services.GetServices<EndpointDataSource>(),
                app.Services.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>());
            AuthSurfaceReport report = await scanner.ScanAsync();
            AuthSurfaceVerifier.VerifyPolicy(report).AssertValid();

            string? payload = await capture;
            Console.WriteLine(payload is null ? "NO_PAYLOAD" : "PAYLOAD=" + payload);
            return suppressed == (payload is null) ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(identityRoot, recursive: true); } catch { }
        }
    }

    private static void SetTelemetryUrl(Uri url)
    {
        Type configType = typeof(Client).Assembly.GetType("KeelMatrix.Telemetry.TelemetryConfig")
            ?? throw new InvalidOperationException("Telemetry configuration type was not found.");
        MethodInfo method = configType.GetMethod(
                "SetUrlOverrideForTests",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Telemetry URL override was not found.");
        method.Invoke(null, [url]);
    }

    private static string CreateIdentityRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "authsurface-telemetry-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string identityToken = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(
            Path.Combine(root, $"Probe-{identityToken}.csproj"),
            $"<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"{identityToken}.csproj\" /></ItemGroup></Project>");
        return root;
    }

    private static void SetTelemetryIdentityStartingPoint(string root)
    {
        Type discoveryType = typeof(Client).Assembly.GetType("KeelMatrix.Telemetry.ProjectIdentity.GitDiscovery")
            ?? throw new InvalidOperationException("Telemetry identity discovery type was not found.");
        MethodInfo method = discoveryType.GetMethod(
                "SetStartingPointsOverrideForTests",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Telemetry identity starting-point override was not found.");
        method.Invoke(null, [new[] { root }]);
    }

    private static async Task<string?> CaptureAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = client.GetStream();
            byte[] headerBytes = await ReadUntilHeadersEndAsync(stream, cancellationToken);
            string headers = Encoding.ASCII.GetString(headerBytes);
            int contentLength = GetContentLength(headers);
            byte[] body = new byte[contentLength];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, cancellationToken);
            return offset == body.Length ? Encoding.UTF8.GetString(body) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadUntilHeadersEndAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return bytes.ToArray();
            }

            bytes.WriteByte(buffer[0]);
            byte[] current = bytes.ToArray();
            if (current.Length >= 4 && current[^4..].AsSpan().SequenceEqual("\r\n\r\n"u8))
            {
                return current;
            }
        }
    }

    private static int GetContentLength(string headers)
    {
        string? line = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        return line is not null && int.TryParse(line[15..].Trim(), out int length) ? length : 0;
    }
}
