using RLoop.Core;
using RLoop.Core.Plugins;

try
{
    var registry = new PluginRegistry(args[0], "0.2.0", new([], [], []));
    var lease = registry.OpenVerified();
    if (args[1] == "hold")
    {
        Console.WriteLine("READY");
        Console.Out.Flush();
        Console.ReadLine();
        GC.KeepAlive(lease);
        // Deliberately do not Dispose: the OS must release the lock on process exit.
        Environment.Exit(0);
    }
    lease.Dispose();
    return 0;
}
catch (RLoopException error)
{
    Console.Error.WriteLine(error.Code);
    return error.Code == "PLUGIN_PROJECT_BUSY" ? 7 : 20;
}
