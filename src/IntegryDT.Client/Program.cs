using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

const string clientVersion = "0.3.0";
const string supportedSdkProtocol = "1";
var baseUrl = Environment.GetEnvironmentVariable("INTEGRY_BACKEND_URL") ?? "http://127.0.0.1:5080";
var companySlug = Environment.GetEnvironmentVariable("INTEGRY_COMPANY_SLUG") ?? "integry-dt-development";
var enrollmentKey = Environment.GetEnvironmentVariable("INTEGRY_CLIENT_ENROLLMENT_KEY") ?? throw new InvalidOperationException("INTEGRY_CLIENT_ENROLLMENT_KEY is required");
var cacheRoot = Environment.GetEnvironmentVariable("INTEGRY_GADGET_CACHE") ?? @"C:\ProgramData\IntegryDT\GadgetCache";
var publicKeyPath = Environment.GetEnvironmentVariable("INTEGRY_GADGET_PUBLIC_KEY") ?? @"C:\ProgramData\IntegryDT\Keys\gadget-public.pem";
var clientRoot = @"C:\ProgramData\IntegryDT\Client";
Directory.CreateDirectory(clientRoot);
Directory.CreateDirectory(cacheRoot);
var statePath = Path.Combine(clientRoot, "device.json");
var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
DeviceState? state = File.Exists(statePath) ? JsonSerializer.Deserialize<DeviceState>(await File.ReadAllTextAsync(statePath)) : null;

if (state is null)
{
    var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/enroll");
    request.Headers.Add("X-Enrollment-Key", enrollmentKey);
    request.Content = JsonContent.Create(new
    {
        companySlug,
        name = Environment.MachineName,
        machineKey = Environment.MachineName,
        os = Environment.OSVersion.VersionString,
        architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        agentVersion = clientVersion
    });
    var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    state = await response.Content.ReadFromJsonAsync<DeviceState>() ?? throw new InvalidOperationException("Invalid enroll response");
    await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state));
    Console.WriteLine($"Device enrolled: {state.DeviceId}");
}

while (true)
{
    try
    {
        using var ws = new ClientWebSocket();
        using var sender = new SocketSender(ws);
        ws.Options.SetRequestHeader("Authorization", $"Bearer {state.DeviceToken}");
        var wsBase = baseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
        await ws.ConnectAsync(new Uri($"{wsBase}/api/v1/agent/connect?deviceId={Uri.EscapeDataString(state.DeviceId)}"), CancellationToken.None);
        Console.WriteLine("Connected to Integry DT Backend.");

        using var connectionCts = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!connectionCts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await sender.SendAsync(new { type = "heartbeat", utc = DateTime.UtcNow }, connectionCts.Token);
                await Task.Delay(TimeSpan.FromSeconds(10), connectionCts.Token);
            }
        }, connectionCts.Token);

        Task? activeJobTask = null;
        CancellationTokenSource? activeJobCts = null;
        string? activeJobId = null;
        var buffer = new byte[131072];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                if (activeJobTask is { IsCompleted: true })
                {
                    try { await activeJobTask; } catch (Exception ex) { Console.Error.WriteLine($"Active job task error: {ex.Message}"); }
                    activeJobTask = null;
                    activeJobCts?.Dispose();
                    activeJobCts = null;
                    activeJobId = null;
                }

                var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (!r.EndOfMessage) throw new InvalidDataException("Backend message exceeds supported frame size.");
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, r.Count));
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                if (type == "cancelJob")
                {
                    var jobId = root.GetProperty("jobId").GetString();
                    if (!string.IsNullOrWhiteSpace(jobId) && jobId == activeJobId && activeJobCts is not null)
                    {
                        Console.WriteLine($"Cancellation requested: {jobId}");
                        activeJobCts.Cancel();
                    }
                    continue;
                }

                if (type != "job") continue;
                if (activeJobTask is not null && !activeJobTask.IsCompleted)
                {
                    Console.Error.WriteLine("Ignoring unexpected concurrent job while another job is active.");
                    continue;
                }

                var job = root.GetProperty("job").Clone();
                activeJobId = job.GetProperty("id").GetString()!;
                activeJobCts = new CancellationTokenSource();
                activeJobTask = RunJobAsync(http, state, sender, job, cacheRoot, publicKeyPath, supportedSdkProtocol, clientVersion, activeJobCts.Token);
            }
        }
        finally
        {
            activeJobCts?.Cancel();
            connectionCts.Cancel();
            try { if (activeJobTask is not null) await activeJobTask; } catch { }
            try { await heartbeat; } catch (OperationCanceledException) { }
            activeJobCts?.Dispose();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Connection error: {ex.Message}");
        await Task.Delay(3000);
    }
}

static async Task RunJobAsync(HttpClient http, DeviceState state, SocketSender sender, JsonElement job, string cacheRoot, string publicKeyPath, string supportedSdkProtocol, string clientVersion, CancellationToken cancellationToken)
{
    var jobId = job.GetProperty("id").GetString()!;
    Console.WriteLine($"Job received: {jobId} / {job.GetProperty("gadgetCode").GetString()} {job.GetProperty("version").GetString()}");
    try
    {
        cancellationToken.ThrowIfCancellationRequested();
        await sender.SendAsync(new { type = "gadgetDownloading", jobId }, CancellationToken.None);
        var execution = await PrepareGadgetAsync(http, state, job, cacheRoot, publicKeyPath, supportedSdkProtocol, cancellationToken);
        await sender.SendAsync(new { type = "gadgetVerified", jobId, versionId = execution.VersionId }, CancellationToken.None);
        await sender.SendAsync(new { type = "jobStarted", jobId }, CancellationToken.None);
        var result = await ExecuteAsync(execution, job, clientVersion, cancellationToken);

        if (result.Result is JsonElement resultElement && resultElement.ValueKind == JsonValueKind.Object && resultElement.TryGetProperty("artifactPath", out var artifactPathElement))
        {
            var artifactPath = artifactPathElement.GetString();
            if (!string.IsNullOrWhiteSpace(artifactPath) && File.Exists(artifactPath))
                await UploadArtifactAsync(http, state, jobId, artifactPath, cancellationToken);
        }

        await sender.SendAsync(new { type = "jobResult", jobId, success = result.ExitCode == 0, stdout = result.Stdout, stderr = result.Stderr, result = result.Result }, CancellationToken.None);
        Console.WriteLine($"Job completed: {jobId}, exit={result.ExitCode}");
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        await sender.SendAsync(new { type = "jobCancelled", jobId }, CancellationToken.None);
        Console.WriteLine($"Job cancelled: {jobId}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Job failed {jobId}: {ex.Message}");
        await sender.SendAsync(new { type = "jobResult", jobId, success = false, stdout = "", stderr = ex.ToString(), result = (object?)null }, CancellationToken.None);
    }
}

static async Task UploadArtifactAsync(HttpClient http, DeviceState state, string jobId, string path, CancellationToken cancellationToken)
{
    using (var stream = File.OpenRead(path))
    using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agent/job-artifacts/{Uri.EscapeDataString(jobId)}?deviceId={Uri.EscapeDataString(state.DeviceId)}"))
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.DeviceToken);
        request.Headers.Add("X-Artifact-Name", Path.GetFileName(path));
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
    File.Delete(path);
}

static async Task<GadgetExecution> PrepareGadgetAsync(HttpClient http, DeviceState state, JsonElement job, string cacheRoot, string publicKeyPath, string supportedSdkProtocol, CancellationToken cancellationToken)
{
    var gadgetCode = job.GetProperty("gadgetCode").GetString() ?? throw new InvalidOperationException("Missing gadgetCode");
    var version = job.GetProperty("version").GetString() ?? throw new InvalidOperationException("Missing version");
    var versionId = job.GetProperty("versionId").GetString() ?? throw new InvalidOperationException("Missing versionId");
    var expectedHash = job.GetProperty("sha256").GetString() ?? throw new InvalidOperationException("Missing SHA256");
    var signatureBase64 = job.GetProperty("signatureBase64").GetString() ?? throw new InvalidOperationException("Missing signature");
    var packageUrl = job.GetProperty("packageUrl").GetString() ?? throw new InvalidOperationException("Missing package URL");
    if (!File.Exists(publicKeyPath)) throw new FileNotFoundException("Integry DT gadget public key not found", publicKeyPath);

    var versionDir = Path.Combine(cacheRoot, gadgetCode, version);
    Directory.CreateDirectory(versionDir);
    var zipPath = Path.Combine(versionDir, "package.zip");
    var runtimeDir = Path.Combine(versionDir, "runtime");

    var validCache = File.Exists(zipPath) && await VerifyPackageAsync(zipPath, expectedHash, signatureBase64, publicKeyPath);
    if (!validCache)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        if (Directory.Exists(runtimeDir)) Directory.Delete(runtimeDir, true);
        using var request = new HttpRequestMessage(HttpMethod.Get, packageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.DeviceToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var output = File.Create(zipPath))
        {
            await response.Content.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        if (!await VerifyPackageAsync(zipPath, expectedHash, signatureBase64, publicKeyPath))
        {
            File.Delete(zipPath);
            throw new CryptographicException("Gadget package hash/signature validation failed");
        }
    }

    cancellationToken.ThrowIfCancellationRequested();
    if (Directory.Exists(runtimeDir)) Directory.Delete(runtimeDir, true);
    Directory.CreateDirectory(runtimeDir);
    ZipFile.ExtractToDirectory(zipPath, runtimeDir);
    var manifestPath = Path.Combine(runtimeDir, "manifest.json");
    if (!File.Exists(manifestPath)) throw new InvalidDataException("manifest.json missing");
    using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
    var manifest = manifestDoc.RootElement;
    if (manifest.GetProperty("gadgetId").GetString() != gadgetCode) throw new InvalidDataException("Gadget id mismatch");
    if (manifest.GetProperty("version").GetString() != version) throw new InvalidDataException("Gadget version mismatch");
    if (manifest.GetProperty("sdkProtocolVersion").GetString() != supportedSdkProtocol) throw new InvalidDataException("Unsupported gadget SDK protocol");
    if (!string.Equals(manifest.GetProperty("platform").GetString(), "windows", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported platform");
    var entrypoint = manifest.GetProperty("entrypoint").GetString() ?? throw new InvalidDataException("Missing entrypoint");
    var executable = Path.Combine(runtimeDir, "payload", entrypoint);
    if (!File.Exists(executable)) throw new FileNotFoundException("Gadget entrypoint missing", executable);
    return new GadgetExecution(versionId, executable, runtimeDir);
}

static async Task<bool> VerifyPackageAsync(string path, string expectedHash, string signatureBase64, string publicKeyPath)
{
    var bytes = await File.ReadAllBytesAsync(path);
    var hash = SHA256.HashData(bytes);
    if (!string.Equals(Convert.ToHexString(hash), expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
    using var rsa = RSA.Create();
    rsa.ImportFromPem(await File.ReadAllTextAsync(publicKeyPath));
    return rsa.VerifyHash(hash, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
}

static async Task<ExecutionResult> ExecuteAsync(GadgetExecution execution, JsonElement job, string clientVersion, CancellationToken cancellationToken)
{
    var psi = new System.Diagnostics.ProcessStartInfo(execution.Executable)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(execution.Executable)!
    };
    psi.Environment["INTEGRY_CLIENT_VERSION"] = clientVersion;
    using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Could not start gadget");
    var input = JsonSerializer.Serialize(new
    {
        jobId = job.GetProperty("id").GetString(),
        action = job.GetProperty("action").GetString(),
        parameters = job.TryGetProperty("parameters", out var p) ? p : default(JsonElement?)
    });
    await process.StandardInput.WriteLineAsync(input);
    process.StandardInput.Close();
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
    try
    {
        await process.WaitForExitAsync(linked.Token);
    }
    catch (OperationCanceledException)
    {
        try { process.Kill(true); } catch { }
        if (cancellationToken.IsCancellationRequested) throw;
        throw new TimeoutException("Gadget execution exceeded 60 seconds.");
    }
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    JsonElement? result = null;
    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
    {
        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        result = JsonDocument.Parse(lines[^1]).RootElement.Clone();
    }
    return new ExecutionResult(process.ExitCode, stdout, stderr, result);
}

public sealed record DeviceState(string DeviceId, string DeviceToken);
public sealed record GadgetExecution(string VersionId, string Executable, string RuntimeDirectory);
public sealed record ExecutionResult(int ExitCode, string Stdout, string Stderr, JsonElement? Result);

public sealed class SocketSender(ClientWebSocket socket) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task SendAsync(object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await _gate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}


