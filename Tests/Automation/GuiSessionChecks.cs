using System.Diagnostics;
using FModel.Settings;
using Newtonsoft.Json.Linq;

internal static class GuiSessionChecks
{
    public static async Task Run(string executable, string profiles, JObject args, RpcClient client)
    {
        await using var other = await RpcClient.Start(executable, profiles);
        var before = await client.Call("session_status", args);
        var profilePath = System.IO.Directory.EnumerateFiles(profiles, "*.json")
            .Single(path => JObject.Parse(System.IO.File.ReadAllText(path)).GetValue(nameof(GameProfile.Id), StringComparison.OrdinalIgnoreCase)?.Value<string>() == args.Value<string>("profileId"));
        var saved = await System.IO.File.ReadAllBytesAsync(profilePath);
        var settings = await client.Call("get_export_settings", args);
        var original = settings["settings"]?.Single(x => x.Value<string>("name") == nameof(UserSettings.KeepDirectoryStructure))["value"];
        // Keep a transient setting to detect an accidental profile reload during window creation.
        if (original == null) throw new Exception("Export settings did not contain KeepDirectoryStructure.");
        await client.Call("set_export_settings", new JObject(args)
        {
            ["values"] = new JObject { ["KeepDirectoryStructure"] = !original.Value<bool>() }
        });
        var changed = await client.Call("get_export_settings", args);
        var expected = await client.Call("list_directory", new JObject(args) { ["limit"] = 5 });
        using var host = Process.GetProcessById(before.Value<int>("processId"));
        try
        {
            // This is the same --game entry point used by the launcher's Open command.
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--game=" + args.Value<string>("profileId"));
            start.Environment["FMODEL_PROFILES_DIRECTORY"] = profiles;
            using var activation = Process.Start(start)!;
            var reads = Task.Run(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    var page = await other.Call("list_directory", new JObject(args) { ["limit"] = 5 });
                    Require(JToken.DeepEquals(expected, page), "Other clients retain their query results during GUI handoff");
                }
            });
            await activation.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(210));
            Require(activation.ExitCode == 0, "Launcher activation exits successfully");
            await reads;
            var shown = await client.Call("session_status", args);
            Require(shown.Value<int>("processId") == host.Id && shown.Value<bool>("hasGui") &&
                shown.Value<int>("files") == before.Value<int>("files"), "GUI appears in the original mounted session");
            host.Refresh();
            Require(host.MainWindowHandle != IntPtr.Zero, "The existing host has a native main window");
            Require(JToken.DeepEquals(changed, await client.Call("get_export_settings", args)), "In-memory settings survive promotion without reloading the saved profile");
            await client.ExpectError("close_session", args);
            await client.Call("select_archives", new JObject(args) { ["archives"] = new JArray() });
            Require((await other.Call("list_directory", args)).Value<int>("total") == 0, "Empty archive selection is applied while the GUI is visible");
            for (var i = 0; i < 2; i++)
                await client.Call("select_archives", new JObject(args) { ["reset"] = true });
            Require(JToken.DeepEquals(expected, await other.Call("list_directory", new JObject(args) { ["limit"] = 5 })), "Repeated archive reset preserves the visible selection");
            var reopened = await Task.WhenAll(client.Call("show_gui", args), other.Call("show_gui", args));
            Require(reopened.All(x => x.Value<int>("processId") == host.Id), "Repeated show requests reuse the same host");
            Require(host.CloseMainWindow(), "The promoted window accepts its normal close action");
            await WaitHidden(client, args, host.Id);
            Require(JToken.DeepEquals(expected, await other.Call("list_directory", new JObject(args) { ["limit"] = 5 })), "Closing the GUI leaves other clients connected");
            await client.Call("show_gui", args);
            host.Refresh();
            Require(host.MainWindowHandle != IntPtr.Zero && host.CloseMainWindow(), "The hidden window can be shown again without restarting");
            await WaitHidden(client, args, host.Id);
            var after = await System.IO.File.ReadAllBytesAsync(profilePath);
            Require(saved.SequenceEqual(after), "Showing and hiding the GUI does not save transient settings");
            await client.Call("set_export_settings", new JObject(args) { ["values"] = new JObject { ["KeepDirectoryStructure"] = original } });
        }
        finally
        {
            host.Refresh();
            if (!host.HasExited && host.MainWindowHandle != IntPtr.Zero)
            {
                host.CloseMainWindow();
                await WaitHidden(client, args, host.Id);
            }
        }
    }

    private static async Task WaitHidden(RpcClient client, JObject args, int processId)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            var status = await client.Call("session_status", args);
            if (status.Value<int>("processId") != processId) throw new Exception("The host was restarted.");
            if (!status.Value<bool>("hasGui")) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The promoted GUI did not hide.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
