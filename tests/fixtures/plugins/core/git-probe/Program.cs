using System.Reflection;
using System.Text.Json;
using RLoop.Core;
using RLoop.Core.Plugins;

try
{
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
