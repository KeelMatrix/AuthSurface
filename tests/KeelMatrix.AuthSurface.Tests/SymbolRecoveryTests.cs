using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class SymbolRecoveryTests
{
    private static readonly string[] symbolUpload = ["/symbols"];
    private static readonly string[] modernTypes = ["Other/1.0.0", "SymbolPackagePublish/4.9.0"];

    [Fact]
    public void CommittedRecoveryArchivePreservesTheReviewedBytes()
    {
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "release-assets", "0.1.0", "symbol-recovery.json")));
        JsonElement identity = contract.RootElement;
        byte[] bytes = File.ReadAllBytes(Path.Combine(Root, identity.GetProperty("archive").GetString()!));
        Assert.Equal(25310, bytes.Length);
        Assert.Equal("8bd933ea649f9930bacb29afb98143b83e81cef5b501b36dff67fa7c4a51ea1b", Fixture.Hash(bytes));
        Assert.Equal(Fixture.Hash(bytes), identity.GetProperty("archiveSha256").GetString());
        Assert.Equal(bytes.Length, identity.GetProperty("archiveSize").GetInt32());
        Assert.Equal("unknown", identity.GetProperty("originalWholeArchiveEquality").GetString());
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using Stream pdb = zip.GetEntry(identity.GetProperty("pdbPath").GetString()!)!.Open();
        using var pdbBytes = new MemoryStream();
        pdb.CopyTo(pdbBytes);
        Assert.Equal("9083d378699419101c856b6c2236a9af12358168aa684bc64c66e8bb93e792d5", Fixture.Hash(pdbBytes.ToArray()));
        Assert.Equal(Fixture.Hash(pdbBytes.ToArray()), identity.GetProperty("pdbSha256").GetString());
    }
    [Theory]
    [InlineData(201, "accepted")]
    [InlineData(200, "unknown")]
    [InlineData(202, "unknown")]
    [InlineData(204, "unknown")]
    [InlineData(301, "unknown")]
    [InlineData(400, "rejected")]
    [InlineData(401, "rejected")]
    [InlineData(403, "rejected")]
    [InlineData(404, "rejected")]
    [InlineData(409, "conflict")]
    [InlineData(413, "rejected")]
    [InlineData(429, "unknown")]
    [InlineData(500, "unknown")]
    [InlineData(503, "unknown")]
    [InlineData(0, "unknown")]
    public async Task RealProtocolRetainsTypedEffectsWithoutApplicationReplay(int status, string effect)
    {
        using var files = await Fixture.Create();
        using var feed = new Feed(files, status);
        files.Bind(feed);
        Result prepared = await files.Invoke("Prepare");
        Assert.True(prepared.ExitCode == 0, prepared.Output);
        Assert.Empty(feed.Uploads);
        Assert.Equal(files.SymbolBytes, File.ReadAllBytes(files.RetainedArchive));
        Result submitted = await files.Invoke("Submit", token: true);
        Assert.Equal(status == 201, submitted.ExitCode == 0);
        Assert.Equal(symbolUpload, feed.Uploads.ToArray());
        Assert.Equal(3, feed.Requests.Count);
        Assert.Empty(feed.InvalidUploads);
        using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(files.Receipt));
        Assert.Equal(effect, receipt.RootElement.GetProperty("effect").GetString());
        if (status == 0) Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("httpStatus").ValueKind);
        else Assert.Equal(status, receipt.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Equal(Fixture.Hash(files.SymbolBytes), receipt.RootElement.GetProperty("archiveSha256").GetString());
        Assert.Equal(1, receipt.RootElement.GetProperty("applicationSendCalls").GetInt32());
        Assert.Equal(0, receipt.RootElement.GetProperty("applicationRetries").GetInt32());
        Assert.Equal("unverified", receipt.RootElement.GetProperty("symbolIndexing").GetString());
        Assert.DoesNotContain("synthetic-local-key", submitted.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-body", submitted.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-local-key", File.ReadAllText(files.Receipt), StringComparison.Ordinal);
        Result replay = await files.Invoke("Submit", token: true);
        Assert.NotEqual(0, replay.ExitCode);
        Assert.Single(feed.Uploads);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("type-array")]
    [InlineData("same-duplicate")]
    [InlineData("no-primary")]
    public async Task DiscoveryAcceptsOnlyTheBoundModernSymbolEndpoint(string discovery)
    {
        using var files = await Fixture.Create();
        using var feed = new Feed(files, discovery: discovery);
        files.Bind(feed);
        Result result = await files.Invoke("Prepare");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Empty(feed.Uploads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-version")]
    [InlineData("relative")]
    [InlineData("conflicting-duplicate")]
    [InlineData("null-id")]
    [InlineData("array-id")]
    [InlineData("missing-type")]
    [InlineData("null-resource")]
    [InlineData("bad-json")]
    [InlineData("root-array")]
    [InlineData("root-null")]
    [InlineData("null-resources")]
    [InlineData("scalar-resources")]
    [InlineData("wrong-index-version")]
    [InlineData("legacy")]
    [InlineData("ftp")]
    [InlineData("non-loopback-http")]
    [InlineData("userinfo")]
    [InlineData("redirect")]
    [InlineData("index-error")]
    [InlineData("index-drop")]
    public async Task MissingMalformedOrInvalidDiscoveryRejectsBeforeAuthentication(string discovery)
    {
        using var files = await Fixture.Create();
        using var feed = new Feed(files, discovery: discovery);
        files.Bind(feed);
        Result result = await files.Invoke("Prepare");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(feed.Uploads);
        Assert.False(Directory.Exists(files.Output));
        Assert.DoesNotContain("GET /primary.nupkg", feed.Requests);
        Assert.All(feed.Requests, request => Assert.Equal("GET /v3/index.json", request));
    }

    [Theory]
    [InlineData("RECOVERY_ARCHIVE_SHA256")]
    [InlineData("RECOVERY_PACKAGE_ID")]
    [InlineData("RECOVERY_PACKAGE_VERSION")]
    [InlineData("RECOVERY_SOURCE_COMMIT")]
    [InlineData("RECOVERY_SOURCE_TAG")]
    [InlineData("RECOVERY_SERVICE_INDEX")]
    [InlineData("RECOVERY_CANDIDATE_COMMIT")]
    [InlineData("GITHUB_EVENT_NAME")]
    [InlineData("GITHUB_REF")]
    [InlineData("GITHUB_REPOSITORY")]
    [InlineData("GITHUB_SHA")]
    [InlineData("GITHUB_RUN_ATTEMPT")]
    public async Task WrongBoundInputsCannotReachDiscovery(string field)
    {
        using var files = await Fixture.Create();
        using var feed = new Feed(files);
        files.Bind(feed);
        files.Environment[field] = "wrong'; throw 'injected'";
        Result result = await files.Invoke("Prepare");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(feed.Requests);
    }

    [Theory]
    [InlineData("archive-missing")]
    [InlineData("archive-corrupt")]
    [InlineData("archive-size")]
    [InlineData("archive-path")]
    [InlineData("nuspec-id")]
    [InlineData("nuspec-version")]
    [InlineData("nuspec-source")]
    [InlineData("pdb")]
    [InlineData("tag-target")]
    [InlineData("unsafe-endpoint")]
    [InlineData("public-primary")]
    [InlineData("public-dll")]
    public async Task WrongArtifactsMetadataSourceAndPublicIdentityReject(string fault)
    {
        using var files = await Fixture.Create();
        using var feed = new Feed(files);
        files.Bind(feed);
        switch (fault)
        {
            case "archive-missing": File.Delete(files.Archive); break;
            case "archive-corrupt": File.AppendAllText(files.Archive, "changed"); break;
            case "archive-size": files.Contract["archiveSize"] = 1; break;
            case "archive-path": files.Contract["archive"] = "../outside.snupkg"; break;
            case "tag-target":
                files.Contract["sourceCommit"] = new string('a', 40);
                files.Environment["RECOVERY_SOURCE_COMMIT"] = new string('a', 40);
                break;
            case "unsafe-endpoint": files.Contract["symbolEndpoint"] = "http://example.com/symbols"; break;
            case "public-primary": files.Contract["primarySha256"] = new string('a', 64); break;
            case "public-dll": files.Contract["dllSha256"] = new string('a', 64); break;
            default:
                string nuspec = files.Nuspec;
                if (fault == "nuspec-id") nuspec = nuspec.Replace("Local.SymbolProbe", "Wrong.Id", StringComparison.Ordinal);
                if (fault == "nuspec-version") nuspec = nuspec.Replace("<version>0.1.0", "<version>0.2.0", StringComparison.Ordinal);
                if (fault == "nuspec-source") nuspec = nuspec.Replace(files.Commit, new string('a', 40), StringComparison.Ordinal);
                files.WriteSymbols(nuspec, fault == "pdb" ? "wrong PDB" : "synthetic PDB");
                break;
        }
        files.SaveContract();
        Result result = await files.Invoke("Prepare");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(feed.Uploads);
        Assert.False(Directory.Exists(files.Output));
        if (!fault.StartsWith("public-", StringComparison.Ordinal)) Assert.Empty(feed.Requests);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("contract")]
    [InlineData("prepared")]
    [InlineData("run")]
    [InlineData("token")]
    public async Task SubmitRejectsChangedRetainedEvidenceOrMissingToken(string fault)
    {
        using var files = await Fixture.Create();
        using var feed = new Feed(files);
        files.Bind(feed);
        Result prepared = await files.Invoke("Prepare");
        Assert.True(prepared.ExitCode == 0, prepared.Output);
        if (fault == "archive") File.AppendAllText(files.RetainedArchive, "changed");
        if (fault == "contract") File.AppendAllText(Path.Combine(files.Output, "symbol-recovery.json"), " ");
        if (fault == "prepared") File.WriteAllText(Path.Combine(files.Output, "prepared.json"), "{}");
        if (fault == "run") files.Environment["GITHUB_RUN_ID"] = "2";
        Result submitted = await files.Invoke("Submit", token: fault != "token");
        Assert.NotEqual(0, submitted.ExitCode);
        Assert.Empty(feed.Uploads);
        Assert.False(File.Exists(files.Receipt));
    }

    [Theory]
    [InlineData(false, 201)]
    [InlineData(true, 201)]
    [InlineData(false, 409)]
    [InlineData(false, 503)]
    public async Task ActualWorkflowRunBlocksPropagateFailuresAndRetainBeforeLogin(bool retentionFails, int status)
    {
        using var files = await Fixture.Create();
        using var feed = new Feed(files, status);
        files.Bind(feed);
        string workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
        string prepare = RunBlock(workflow, "Prepare exact symbols recovery");
        string submit = RunBlock(workflow, "Submit exact symbols only");
        string driver = "$ErrorActionPreference='Stop'\n" + prepare + "\n" +
            (retentionFails ? "throw 'synthetic retention failure'\n" : "") +
            "Set-Content login-marker 'synthetic login'\n$env:NUGET_PUSH_TOKEN='synthetic-local-key'\n" + submit;
        File.WriteAllText(Path.Combine(files.Directory, "driver.ps1"), driver);
        Result result = await files.Run("pwsh", "-NoProfile", "-File", Path.Combine(files.Directory, "driver.ps1"));
        Assert.Equal(!retentionFails && status == 201, result.ExitCode == 0);
        Assert.Equal(!retentionFails, File.Exists(Path.Combine(files.Directory, "login-marker")));
        Assert.Equal(retentionFails ? 0 : 1, feed.Uploads.Count);
        Assert.Empty(feed.InvalidUploads);
    }

    [Fact]
    public async Task ActualWorkflowConditionsExcludeDispatchAndMalformedEventsFromPrimary()
    {
        string workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
        using var files = await Fixture.Create();
        string driver = "$ErrorActionPreference='Stop'\n$count=0\n";
        foreach (string job in new[] { "release", "recover-symbols" })
        {
            string condition = Regex.Match(workflow, "(?m)^  " + job + ":\\r?\\n    if: \\$\\{\\{ (.+) \\}\\}").Groups[1].Value;
            Assert.NotEmpty(condition);
            string expression = condition.Replace("github.event_name", "$eventName", StringComparison.Ordinal)
                .Replace("github.ref", "$ref", StringComparison.Ordinal).Replace("github.run_attempt", "$attempt", StringComparison.Ordinal)
                .Replace(" == ", " -eq ", StringComparison.Ordinal).Replace(" && ", " -and ", StringComparison.Ordinal);
            expression = Regex.Replace(expression, "startsWith\\(\\$ref, '([^']+)'\\)", "($ref.StartsWith('$1', [StringComparison]::Ordinal))");
            foreach (string eventName in new[] { "push", "workflow_dispatch", "pull_request", "schedule", "workflow_call" })
                foreach (string reference in new[] { "refs/tags/v0.1.0", "refs/heads/main", "refs/heads/topic", "refs/tags/wrong" })
                    foreach (int attempt in new[] { 1, 2, 3 })
                    {
                        bool expected = job == "release" ? eventName == "push" && reference.StartsWith("refs/tags/v", StringComparison.Ordinal)
                            : eventName == "workflow_dispatch" && reference == "refs/heads/main" && attempt == 1;
                        driver += $"$eventName='{eventName}'; $ref='{reference}'; $attempt={attempt}\n" +
                            $"if ([bool]({expression}) -ne ${expected.ToString().ToLowerInvariant()}) {{ throw 'routing failure' }}\n$count++\n";
                    }
        }
        driver += "if ($count -ne 120) {throw 'incomplete routing matrix'}";
        string path = Path.Combine(files.Directory, "routing.ps1");
        File.WriteAllText(path, driver);
        Result result = await files.Run("pwsh", "-NoProfile", "-File", path);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static string RunBlock(string workflow, string name)
    {
        string step = Regex.Match(workflow, "(?ms)^      - name: " + Regex.Escape(name) + ".*?(?=^      - name:|\\z)").Value;
        string block = Regex.Match(step, "(?ms)^        run: \\|\\r?\\n(.*)").Groups[1].Value;
        Assert.NotEmpty(block);
        return Regex.Replace(block, "(?m)^          ", "");
    }

    private static string Root
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root missing.");
        }
    }

    private sealed record Result(int ExitCode, string Output);

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "symbol recovery λ " + Guid.NewGuid().ToString("N"));
        public string Archive => Path.Combine(Directory, "release-assets", "0.1.0", "Local.SymbolProbe.0.1.0.snupkg");
        public string Output => Path.Combine(Directory, "artifacts", "symbol-recovery");
        public string RetainedArchive => Path.Combine(Output, "Local.SymbolProbe.0.1.0.snupkg");
        public string Receipt => Path.Combine(Output, "receipt.json");
        public string ContractPath => Path.Combine(Directory, "release-assets", "0.1.0", "symbol-recovery.json");
        public Dictionary<string, object> Contract { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);
        public byte[] SymbolBytes { get; private set; } = [];
        public byte[] PrimaryBytes { get; private set; } = [];
        public string Commit { get; private set; } = "";
        public string Nuspec => $"<package><metadata><id>Local.SymbolProbe</id><version>0.1.0</version><repository url=\"https://github.com/KeelMatrix/AuthSurface\" commit=\"{Commit}\" /></metadata></package>";

        public static async Task<Fixture> Create()
        {
            var files = new Fixture();
            System.IO.Directory.CreateDirectory(Path.Combine(files.Directory, "scripts"));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(files.Archive)!);
            File.Copy(Path.Combine(Root, "scripts", "recover-symbols.ps1"), Path.Combine(files.Directory, "scripts", "recover-symbols.ps1"));
            foreach (string[] args in new[] {
                new[] { "init", "-q" }, new[] { "-c", "user.name=KeelMatrix", "-c", "user.email=keelmatrix@gmail.com", "commit", "-q", "--allow-empty", "-m", "Initialize synthetic protocol fixture" },
                new[] { "tag", "v0.1.0" } })
            {
                Result result = await files.Run("git", args);
                Assert.True(result.ExitCode == 0, result.Output);
            }
            files.Commit = (await files.Run("git", "rev-parse", "HEAD")).Output.Trim();
            files.Contract["schemaVersion"] = 1;
            files.Contract["archive"] = "release-assets/0.1.0/Local.SymbolProbe.0.1.0.snupkg";
            files.Contract["packageId"] = "Local.SymbolProbe";
            files.Contract["packageVersion"] = "0.1.0";
            files.Contract["sourceCommit"] = files.Commit;
            files.Contract["sourceTag"] = "v0.1.0";
            files.Contract["repository"] = "https://github.com/KeelMatrix/AuthSurface";
            files.Contract["pdbPath"] = "lib/net8.0/probe.pdb";
            files.Contract["pdbSha256"] = Hash(Encoding.UTF8.GetBytes("synthetic PDB"));
            files.Contract["dllPath"] = "lib/net8.0/probe.dll";
            files.Contract["dllSha256"] = Hash(Encoding.UTF8.GetBytes("synthetic DLL"));
            files.Contract["originalWholeArchiveEquality"] = "unknown";
            files.WriteSymbols(files.Nuspec, "synthetic PDB");
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) WriteEntry(zip, "lib/net8.0/probe.dll", "synthetic DLL");
            files.PrimaryBytes = stream.ToArray();
            files.Contract["primarySha256"] = Hash(files.PrimaryBytes);
            files.Environment["GITHUB_EVENT_NAME"] = "workflow_dispatch";
            files.Environment["GITHUB_REF"] = "refs/heads/main";
            files.Environment["GITHUB_REPOSITORY"] = "KeelMatrix/AuthSurface";
            files.Environment["GITHUB_RUN_ATTEMPT"] = "1";
            files.Environment["GITHUB_RUN_ID"] = "1";
            files.Environment["GITHUB_SHA"] = files.Commit;
            files.Environment["RECOVERY_CANDIDATE_COMMIT"] = files.Commit;
            return files;
        }

        public void WriteSymbols(string nuspec, string pdb)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "Local.SymbolProbe.nuspec", nuspec);
                WriteEntry(zip, "lib/net8.0/probe.pdb", pdb);
            }
            SymbolBytes = stream.ToArray();
            File.WriteAllBytes(Archive, SymbolBytes);
            Contract["archiveSize"] = SymbolBytes.Length;
            Contract["archiveSha256"] = Hash(SymbolBytes);
            Environment["RECOVERY_ARCHIVE_SHA256"] = Hash(SymbolBytes);
        }

        private static void WriteEntry(ZipArchive zip, string name, string value)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(value);
        }

        public void Bind(Feed feed)
        {
            Contract["serviceIndex"] = feed.Base + "v3/index.json";
            Contract["symbolEndpoint"] = feed.Base + "symbols";
            Contract["primaryUrl"] = feed.Base + "primary.nupkg";
            foreach (var pair in new[] { ("ARCHIVE_SHA256", "archiveSha256"), ("PACKAGE_ID", "packageId"), ("PACKAGE_VERSION", "packageVersion"),
                ("SOURCE_COMMIT", "sourceCommit"), ("SOURCE_TAG", "sourceTag"), ("SERVICE_INDEX", "serviceIndex") })
                Environment["RECOVERY_" + pair.Item1] = (string)Contract[pair.Item2];
            SaveContract();
        }

        public void SaveContract() => File.WriteAllText(ContractPath, JsonSerializer.Serialize(Contract));
        public Task<Result> Invoke(string mode, bool token = false)
        {
            Environment["NUGET_PUSH_TOKEN"] = token ? "synthetic-local-key" : "";
            return Run("pwsh", "-NoProfile", "-File", Path.Combine(Directory, "scripts", "recover-symbols.ps1"), "-Mode", mode, "-OutputDirectory", Output);
        }
        public async Task<Result> Run(string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
            foreach (var pair in Environment) start.Environment[pair.Key] = pair.Value;
            if (executable == "git")
            {
                start.Environment["GIT_AUTHOR_NAME"] = "KeelMatrix";
                start.Environment["GIT_AUTHOR_EMAIL"] = "keelmatrix@gmail.com";
                start.Environment["GIT_COMMITTER_NAME"] = "KeelMatrix";
                start.Environment["GIT_COMMITTER_EMAIL"] = "keelmatrix@gmail.com";
            }
            start.Environment["KEELMATRIX_NO_TELEMETRY"] = "1";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture process.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            return new Result(process.ExitCode, await stdout + await stderr);
        }
        public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        public void Dispose()
        {
            foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class Feed : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task server;
        public string Base { get; }
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> Uploads { get; } = new();
        public ConcurrentQueue<string> InvalidUploads { get; } = new();
        public Feed(Fixture files, int status = 201, string discovery = "valid")
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Base = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(Base);
            listener.Start();
            server = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                    string path = context.Request.Url!.AbsolutePath;
                    Requests.Enqueue(context.Request.HttpMethod + " " + path);
                    context.Response.StatusCode = 404;
                    if (context.Request.HttpMethod == "GET" && path == "/v3/index.json")
                    {
                        if (discovery == "index-drop") { context.Response.Abort(); continue; }
                        object? endpoint = discovery switch
                        {
                            "relative" => "/symbols",
                            "null-id" => null,
                            "array-id" => new[] { Base + "symbols" },
                            "ftp" => "ftp://example.com/symbols",
                            "non-loopback-http" => "http://example.com/symbols",
                            "userinfo" => "https://user@example.com/symbols",
                            _ => Base + "symbols"
                        };
                        var resources = new List<object?>();
                        if (discovery != "no-primary") resources.Add(new Dictionary<string, object> { ["@id"] = Base + "primary", ["@type"] = "PackagePublish/2.0.0" });
                        var resource = new Dictionary<string, object?>
                        {
                            ["@id"] = endpoint,
                            ["@type"] = discovery == "type-array" ? modernTypes
                            : discovery is "wrong-version" or "legacy" ? "SymbolPackagePublish/2.0.0" : "SymbolPackagePublish/4.9.0"
                        };
                        if (discovery == "missing-type") resource.Remove("@type");
                        if (discovery != "missing") resources.Add(discovery == "null-resource" ? null : resource);
                        if (discovery == "same-duplicate") resources.Add(resource);
                        if (discovery == "conflicting-duplicate") resources.Add(new Dictionary<string, string> { ["@id"] = Base + "other", ["@type"] = "SymbolPackagePublish/4.9.0" });
                        string body = discovery switch
                        {
                            "bad-json" => "broken",
                            "root-array" => "[]",
                            "root-null" => "null",
                            "null-resources" => "{\"version\":\"3.0.0\",\"resources\":null}",
                            "scalar-resources" => "{\"version\":\"3.0.0\",\"resources\":1}",
                            _ => JsonSerializer.Serialize(new { version = discovery == "wrong-index-version" ? "2.0.0" : "3.0.0", resources })
                        };
                        context.Response.StatusCode = discovery == "index-error" ? 503 : discovery == "redirect" ? 302 : 200;
                        context.Response.RedirectLocation = discovery == "redirect" ? Base + "other" : null;
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                    }
                    else if (context.Request.HttpMethod == "GET" && path == "/primary.nupkg")
                    {
                        context.Response.StatusCode = 200;
                        await context.Response.OutputStream.WriteAsync(files.PrimaryBytes);
                    }
                    else if (context.Request.HttpMethod == "PUT")
                    {
                        Uploads.Enqueue(path);
                        using var body = new MemoryStream();
                        await context.Request.InputStream.CopyToAsync(body);
                        if (path != "/symbols" || body.ToArray().AsSpan().IndexOf(files.SymbolBytes) < 0 ||
                            context.Request.Headers["X-NuGet-ApiKey"] != "synthetic-local-key" ||
                            context.Request.ContentType?.StartsWith("multipart/form-data;", StringComparison.Ordinal) != true)
                            InvalidUploads.Enqueue(path);
                        if (status == 0) { context.Response.Abort(); continue; }
                        context.Response.StatusCode = status;
                        context.Response.Headers["x-ms-request-id"] = status == 201 ? "synthetic-local-key" : "synthetic-request-id";
                        if (status == 301) context.Response.RedirectLocation = Base + "primary";
                        if (status != 204) await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("private-response-body"));
                    }
                    context.Response.Close();
                }
            });
        }
        public void Dispose() { listener.Close(); server.GetAwaiter().GetResult(); }
    }
}
