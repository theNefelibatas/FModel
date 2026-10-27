using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

internal static class ClientLifetimeChecks
{
    public static async Task Run(string executable, string profiles, string requestPath, JObject args)
    {
        await File.WriteAllTextAsync(requestPath, new JObject { ["operation"] = "session_status", ["arguments"] = args }.ToString());
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--automation-request");
        start.Environment["FMODEL_PROFILES_DIRECTORY"] = profiles;
        start.Environment["FMODEL_AUTOMATION_REQUEST"] = requestPath;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await using var reconnect = await RpcClient.Start(executable, profiles);
        int? hostId = null;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var status = await reconnect.Call("session_status", args);
            hostId = status.Value<int>("processId");
            await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5));
            if (process.ExitCode != 0 || JObject.Parse(await output).Value<int>("processId") != hostId)
                throw new Exception("One-shot request did not return the surviving host: " + await errors);
            Console.WriteLine("PASS: One-shot client closes stdout/stderr while its host survives and accepts another client");
        }
        finally
        {
            await reconnect.Call("close_session", args);
            if (hostId != null)
            {
                try { using var host = Process.GetProcessById(hostId.Value); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (ArgumentException) { }
            }
        }
    }
}
