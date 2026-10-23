using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FModel.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FModel.Automation;

internal static class LocalProtocol
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    public const string ProfileEnvironment = "FMODEL_AUTOMATION_PROFILE";
    public const string RequestEnvironment = "FMODEL_AUTOMATION_REQUEST";

    public static string PipeName(string profileId)
    {
        var identity = ProfileStore.NormalizeDirectory(ProfileStore.ProfilesDirectory) + ":" + profileId;
        return $"FModel.Automation.v1.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }

    public static async Task WriteAsync(Stream stream, JObject message, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
        if (data.Length > MaxFrameBytes) throw new InvalidOperationException("Response exceeds the transport limit. Request a smaller page or document chunk.");
        await stream.WriteAsync(BitConverter.GetBytes(data.Length), ct);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<JObject> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, ct);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid automation frame size.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return JObject.Parse(Encoding.UTF8.GetString(data));
    }
}

internal sealed class LocalSessionServer(GameOperations operations, string profileId) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task _listener;

    public void Start() => _listener = ListenAsync();

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(LocalProtocol.PipeName(profileId), PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                _ = HandleAsync(pipe);
                pipe = null;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex)
            {
                operations.InitializationError = $"Automation transport failed: {ex.Message}";
                Log.Error(ex, "Automation listener failed");
                break;
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
        using (var monitorStop = new CancellationTokenSource())
        {
            budget.CancelAfter(TimeSpan.FromMinutes(2));
            var close = false;
            Task monitor = null;
            try
            {
                var request = await LocalProtocol.ReadAsync(pipe, budget.Token);
                var operation = request.Value<string>("operation") ?? throw new ArgumentException("Missing operation.");
                var args = request["arguments"] as JObject ?? throw new ArgumentException("Missing arguments.");
                if (args.Value<string>("profileId") != profileId) throw new ArgumentException("Profile ID does not match this session.");
                monitor = MonitorDisconnectAsync(pipe, budget, monitorStop.Token);
                var result = await Task.Run(() => operations.ExecuteAsync(operation, args, budget.Token), budget.Token);
                close = operation == "close_session";
                await LocalProtocol.WriteAsync(pipe, new JObject { ["success"] = true, ["result"] = JToken.FromObject(result) }, budget.Token);
            }
            catch (Exception ex)
            {
                try
                {
                    using var errorBudget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    errorBudget.CancelAfter(TimeSpan.FromSeconds(5));
                    await LocalProtocol.WriteAsync(pipe, new JObject { ["success"] = false, ["error"] = new JObject
                    { ["type"] = ex.GetType().Name, ["message"] = ex.Message } }, errorBudget.Token);
                }
                catch (Exception writeError) { Log.Debug(writeError, "Automation client disconnected"); }
            }
            finally
            {
                monitorStop.Cancel();
                if (monitor != null) await monitor;
            }
            if (close) _ = Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        }
    }

    private static async Task MonitorDisconnectAsync(Stream pipe, CancellationTokenSource operation, CancellationToken ct)
    {
        try
        {
            // One request per connection: EOF or additional request bytes ends the active request.
            await pipe.ReadExactlyAsync(new byte[1], ct);
            operation.Cancel();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { operation.Cancel(); }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        operations.Dispose();
    }
}

internal sealed class SessionBroker : IDisposable
{
    private readonly ConcurrentDictionary<string, Process> _children = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public async Task<JToken> ExecuteAsync(string operation, JObject args, CancellationToken ct)
    {
        OperationCatalog.Validate(operation, args);
        if (operation is "profile_options" or "get_profile" or "create_profile" or "update_profile" or "delete_profile")
            return JToken.FromObject(ProfileOperations.Execute(operation, args));
        if (operation == "capabilities") return JToken.FromObject(new
        {
            operations = OperationCatalog.All.Select(x => new { name = x.Name, description = x.Description, readOnly = x.ReadOnly }),
            limits = new[]
            {
                "Reference scanning covers mounted IoStore import tables, not all soft references.",
                "Automation viewports use an isolated desktop renderer and require OpenGL; they do not control an already open interactive 3D window.",
                "Use show_gui to display an existing session without reloading it, then open_in_gui for asset previews. Clients share the same session state.",
                "Archive selection filters browsing and search; it does not unmount containers or remove reference dependencies.",
                "Socket attachment editing, individual animation tracks, arbitrary material texture editing, image-merger layout and audio-device selection are not fully exposed.",
                "Parser support and required mappings/codecs are the same as the underlying FModel build.",
                "Non-cooperative native parsing may outlive cancellation; a busy session remains locked until it returns."
            }
        });
        var profiles = ProfileStore.Enumerate(out var corrupt);
        if (operation == "profiles") return JToken.FromObject(new
        {
            profiles = profiles.Select(x => new { id = x.Id, name = x.Game.GameName,
                directory = x.Game.GameDirectory, engine = x.Game.UeVersion.ToString(),
                lastUsedAt = x.LastUsedAt, running = ProfileLock.IsHeldByAliveProcess(x.Id) }),
            unreadableProfiles = corrupt.Select(Path.GetFileName)
        });
        var profileId = args.Value<string>("profileId");
        if (!profiles.Any(x => x.Id == profileId)) throw new ArgumentException("Unknown profileId. Call profiles and use an exact returned ID.");

        await _launchGate.WaitAsync(ct);
        NamedPipeClientStream pipe;
        try
        {
            if (_children.TryGetValue(profileId, out var previous) && previous.HasExited)
            {
                _children.TryRemove(profileId, out _);
                previous.Dispose();
            }
            pipe = await ConnectAsync(profileId, 300, ct);
            if (pipe == null)
            {
                if (!ProfileLock.IsHeldByAliveProcess(profileId) && !_children.ContainsKey(profileId))
                {
                    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve the running executable.");
                    // The persistent host must not inherit the client's protocol pipe handles.
                    var start = new ProcessStartInfo(executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                    start.ArgumentList.Add("--automation-host");
                    start.ArgumentList.Add("--game=" + profileId);
                    var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start a game session.");
                    _children[profileId] = child;
                }
                pipe = await ConnectAsync(profileId, 15000, ct)
                    ?? throw new InvalidOperationException("The profile is running without a compatible automation endpoint, or its host failed to start. Restart that profile with this build.");
            }
        }
        finally { _launchGate.Release(); }
        using (pipe)
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // The receiver's operation budget is shorter than the caller's transport budget.
            budget.CancelAfter(TimeSpan.FromMinutes(3));
            await LocalProtocol.WriteAsync(pipe, new JObject { ["operation"] = operation, ["arguments"] = args }, budget.Token);
            var response = await LocalProtocol.ReadAsync(pipe, budget.Token);
            if (response.Value<bool>("success"))
            {
                if (operation == "close_session" && _children.TryRemove(profileId, out var child)) child.Dispose();
                return response["result"];
            }
            throw new InvalidOperationException($"{response["error"]?["type"]}: {response["error"]?["message"]}");
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string profileId, int milliseconds, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", LocalProtocol.PipeName(profileId), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(milliseconds, ct); return pipe; }
        catch (TimeoutException) { pipe.Dispose(); return null; }
        catch { pipe.Dispose(); throw; }
    }

    public void Dispose()
    {
        // Sessions survive a client disconnect so another client can inspect or finish their jobs.
        foreach (var child in _children.Values) child.Dispose();
        _launchGate.Dispose();
    }
}
