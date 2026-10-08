using System.Diagnostics;
using System.Text.Json;

// A harmless, real child-process fixture. This is not the production engine or a Windows driver.
string mode = args.FirstOrDefault() ?? "wait";
if (mode == "exit") { Console.Error.WriteLine("fixture startup error"); return 17; }
if (mode is "protocol" or "protocol-stderr" or "protocol-hang")
{
    if (mode == "protocol-stderr") Console.Error.WriteLine("NORTHPASS_READY protocol=1");
    if (mode == "protocol-hang") { await Task.Delay(TimeSpan.FromMinutes(2)); return 0; }
    if (mode == "protocol") Console.WriteLine("NORTHPASS_READY protocol=1");
    if (await Console.In.ReadLineAsync() == "STOP") { Console.WriteLine("fixture graceful stop"); return 0; }
    return 18;
}
if (mode == "native-parent")
{
    var info = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "--mode", "idle", "--parent-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--stdio-control" }) info.ArgumentList.Add(argument);
    using var child = Process.Start(info)!;
    var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (ready != "NORTHPASS_READY protocol=1") { if (!child.HasExited) child.Kill(); throw new InvalidOperationException("Native parent fixture initialization failed: " + await child.StandardError.ReadToEndAsync()); }
    Console.WriteLine("native-child-pid=" + child.Id);
    // Exit the real owner abruptly while the pipe/driver session is active.
    Environment.Exit(0);
}
Console.WriteLine("fixture ready");
Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));
Console.Error.WriteLine("fixture stderr");
if (mode == "crash") { await Task.Delay(800); return 23; }
if (mode == "spawn")
{
    var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    child.ArgumentList.Add(typeof(Program).Assembly.Location);
    child.ArgumentList.Add("wait");
    using var process = Process.Start(child)!;
    Console.WriteLine("child-pid=" + process.Id);
}
await Task.Delay(TimeSpan.FromMinutes(2));
return 0;
