using System.Diagnostics;
using System.Text.Json;

// A fake plugin program. It records what it received under $RESOLOOP_PLUGIN_DATA so tests can
// compare the exact argv/cwd/environment without capturing the inherited stdout.
var data = Environment.GetEnvironmentVariable("RESOLOOP_PLUGIN_DATA");
switch (args[0])
{
    case "record":
    {
        var environment = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(key => key.StartsWith("RESOLOOP_PLUGIN_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(key => key, key => Environment.GetEnvironmentVariable(key));
        File.WriteAllText(Path.Combine(data!, "received.json"), JsonSerializer.Serialize(new
        {
            argv = args, cwd = Environment.CurrentDirectory, env = environment
        }));
        return int.Parse(args[1]);
    }
    case "spawn":
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("sleep");
        using var grandchild = Process.Start(start)!;
        var pids = Path.Combine(data!, "pids.json");
        File.WriteAllText(pids + ".tmp", JsonSerializer.Serialize(new { child = Environment.ProcessId, grandchild = grandchild.Id }));
        File.Move(pids + ".tmp", pids);
        if (args[1] == "hang") Thread.Sleep(Timeout.Infinite);
        return int.Parse(args[1]);
    }
    case "sleep":
        Thread.Sleep(TimeSpan.FromSeconds(240));
        return 0;
    case "marker":
        File.WriteAllText(args[1], "ran");
        return 0;
    default:
        return 64;
}
