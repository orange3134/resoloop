using System.Reflection;
using System.Text.Json;
using RLoop.Core;
using RLoop.Core.Plugins;

try
{
    if (args[0] is "install" or "remove")
    {
        var registry = new PluginRegistry(args[1], "0.2.0", new PluginNameReservations([], [], []));
        var installer = new PluginInstaller(registry);
        if (args[0] == "install") Console.WriteLine(JsonSerializer.Serialize(installer.Install(args[2])));
        else installer.Remove(args[2], true);
        return 0;
    }
    var type = typeof(PluginSource).Assembly.GetType("RLoop.Core.Plugins.PluginGit", throwOnError: true)!;
    var method = type.GetMethod(args[0] == "local" ? "GetLocalFiles" : "Export", BindingFlags.Public | BindingFlags.Static)!;
    var result = method.Invoke(null, args.Skip(1).Cast<object>().ToArray());
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}
catch (TargetInvocationException exception) when (exception.InnerException is RLoopException error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { error.Code, Diagnostic = error.ToString(), error.Context }));
    return 7;
}

catch (RLoopException error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { error.Code, Diagnostic = error.ToString(), error.Context }));
    return 7;
}
