using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class ReleasePublicationTests
{
    private static readonly string[] pairedUploads = ["/primary/", "/symbols/"];
    private static readonly string[] primaryUpload = ["/primary/"];
    private static readonly string[] symbolUpload = ["/symbols/"];

    [Fact]
    public async Task ConcurrentLocalFeedsOwnDistinctLivePorts()
    {
        using var files = new Packages();
        var feeds = new ConcurrentBag<LocalFeed>();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => feeds.Add(new LocalFeed(files)))));
            Assert.Equal(8, feeds.Select(feed => feed.Base).Distinct(StringComparer.Ordinal).Count());
            using var client = new HttpClient();
            foreach (LocalFeed feed in feeds)
            {
                string index = await client.GetStringAsync(feed.Source);
                Assert.Contains(feed.Base + "primary/", index, StringComparison.Ordinal);
                Assert.Contains(feed.Base + "symbols/", index, StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (LocalFeed feed in feeds) { feed.Dispose(); }
        }
    }

    [Theory]
    [InlineData(201, 201)]
    [InlineData(400, 201)]
    [InlineData(409, 201)]
    [InlineData(201, 400)]
    [InlineData(201, 409)]
    public async Task V3PublicationUploadsEachArchiveOnceAndPropagatesRejection(int primaryStatus, int symbolStatus)
    {
        using var files = new Packages();
        using var feed = new LocalFeed(files, primaryStatus, symbolStatus);
        Result result = await Publish(files, feed.Source);

        Assert.Equal(primaryStatus == 201 && symbolStatus == 201, result.ExitCode == 0);
        Assert.Equal(primaryStatus == 201 ? pairedUploads : primaryUpload, feed.Uploads.ToArray());
        Assert.Empty(feed.InvalidUploads);
        if (result.ExitCode != 0)
        {
            Assert.Contains("do not replay", result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(".nupkg")]
    [InlineData(".snupkg")]
    public async Task MissingArchiveRejectsBeforeAnyFeedRequest(string missingExtension)
    {
        using var files = new Packages();
        File.Delete(Path.ChangeExtension(files.Primary, missingExtension));
        using var feed = new LocalFeed(files);
        Result result = await Publish(files, feed.Source);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(feed.Requests);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("no-symbols")]
    [InlineData("no-primary")]
    [InlineData("invalid-symbol-id")]
    [InlineData("invalid-json")]
    [InlineData("index-rejection")]
    public async Task UnsupportedSourceRejectsBeforeUpload(string sourceCase)
    {
        using var files = new Packages();
        using var feed = new LocalFeed(files, sourceCase: sourceCase);
        Result result = await Publish(files, sourceCase == "raw" ? feed.Base + "primary/" : feed.Source);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(feed.Uploads);
    }

    [Fact]
    public async Task RawSymbolSourceReproducesSilentLegacyOmission()
    {
        using var files = new Packages();
        using var feed = new LocalFeed(files);
        Result result = await Run("dotnet", "nuget", "push", files.Primary, "--source", feed.Source,
            "--api-key", "synthetic-local-key", "--symbol-api-key", "synthetic-local-key",
            "--symbol-source", feed.Base + "symbols/", "--timeout", "10");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(primaryUpload, feed.Uploads.ToArray());
        Assert.Empty(feed.InvalidUploads);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(400)]
    [InlineData(409)]
    public async Task ExplicitSnupkgRecoveryCommandNeverUploadsPrimary(int symbolStatus)
    {
        using var files = new Packages();
        using var feed = new LocalFeed(files, symbolStatus: symbolStatus);
        Result result = await Run("dotnet", "nuget", "push", Path.ChangeExtension(files.Primary, ".snupkg"),
            "--source", feed.Source, "--api-key", "synthetic-local-key",
            "--symbol-api-key", "synthetic-local-key", "--timeout", "10");
        Assert.Equal(symbolStatus == 201, result.ExitCode == 0);
        Assert.Equal(symbolUpload, feed.Uploads.ToArray());
        Assert.Empty(feed.InvalidUploads);
    }

    private static Task<Result> Publish(Packages files, string source) => Run("pwsh",
        "-NoProfile", "-File", Path.Combine(RepositoryRoot, "scripts", "push-release-package.ps1"),
        "-PackagePath", files.Primary, "-Source", source);

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                directory = directory.Parent;
            }
            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static async Task<Result> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["NUGET_PUSH_TOKEN"] = "synthetic-local-key";
        start.Environment["KEELMATRIX_NO_TELEMETRY"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DO_NOT_TRACK"] = "1";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start client.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Local publication client timed out.");
        }
        return new Result(process.ExitCode, await stdout + await stderr);
    }

    private sealed record Result(int ExitCode, string Output);

    private sealed class Packages : IDisposable
    {
        // Include spaces and Unicode so every client invocation must preserve argument boundaries.
        private readonly string directory = Path.Combine(Path.GetTempPath(), "authsurface publication λ " + Guid.NewGuid().ToString("N"));
        public string Primary => Path.Combine(directory, "Local.SymbolProbe.0.0.0.nupkg");
        public byte[] PrimaryBytes { get; }
        public byte[] SymbolBytes { get; }

        public Packages()
        {
            Directory.CreateDirectory(directory);
            foreach (string extension in new[] { ".nupkg", ".snupkg" })
            {
                using var archive = ZipFile.Open(Path.ChangeExtension(Primary, extension), ZipArchiveMode.Create);
                using (var writer = new StreamWriter(archive.CreateEntry("Local.SymbolProbe.nuspec").Open()))
                {
                    writer.Write("<package><metadata><id>Local.SymbolProbe</id><version>0.0.0</version><authors>Local</authors><description>Synthetic protocol test.</description></metadata></package>");
                }
                using var content = new StreamWriter(archive.CreateEntry("lib/net8.0/probe" + (extension == ".nupkg" ? ".dll" : ".pdb")).Open());
                content.Write("Synthetic local " + extension);
            }
            PrimaryBytes = File.ReadAllBytes(Primary);
            SymbolBytes = File.ReadAllBytes(Path.ChangeExtension(Primary, ".snupkg"));
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class LocalFeed : IDisposable
    {
        private readonly WebApplication server;
        public string Base { get; }
        public string Source => Base + "v3/index.json";
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> Uploads { get; } = new();
        public ConcurrentQueue<string> InvalidUploads { get; } = new();

        public LocalFeed(Packages files, int primaryStatus = 201, int symbolStatus = 201, string sourceCase = "valid")
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            // Bind once and keep the kernel-selected port for the server's lifetime.
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            server = builder.Build();
            server.Run(async context =>
            {
                string path = context.Request.Path.Value!;
                Requests.Enqueue(context.Request.Method + " " + path);
                context.Response.StatusCode = 404;
                if (context.Request.Method == "GET" && path == "/v3/index.json")
                {
                    var resources = new List<Dictionary<string, string>>();
                    if (sourceCase != "no-primary")
                    {
                        resources.Add(new() { ["@id"] = Base + "primary/", ["@type"] = "PackagePublish/2.0.0" });
                    }
                    if (sourceCase != "no-symbols")
                    {
                        resources.Add(new() { ["@id"] = sourceCase == "invalid-symbol-id" ? "invalid" : Base + "symbols/", ["@type"] = "SymbolPackagePublish/4.9.0" });
                    }
                    byte[] body = Encoding.UTF8.GetBytes(sourceCase == "invalid-json" ? "invalid" : JsonSerializer.Serialize(new { version = "3.0.0", resources }));
                    context.Response.StatusCode = sourceCase == "index-rejection" ? 403 : 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength = body.Length;
                    await context.Response.Body.WriteAsync(body);
                }
                else if (context.Request.Method == "PUT")
                {
                    Uploads.Enqueue(path);
                    using var body = new MemoryStream();
                    await context.Request.Body.CopyToAsync(body);
                    byte[] expected = path == "/symbols/" ? files.SymbolBytes : files.PrimaryBytes;
                    if (path is not ("/primary/" or "/symbols/") || body.ToArray().AsSpan().IndexOf(expected) < 0)
                    {
                        InvalidUploads.Enqueue(path);
                    }
                    context.Response.StatusCode = path == "/primary/" ? primaryStatus : symbolStatus;
                }
            });
            server.StartAsync().GetAwaiter().GetResult();
            Base = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
        }

        public void Dispose()
        {
            server.StopAsync().GetAwaiter().GetResult();
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
