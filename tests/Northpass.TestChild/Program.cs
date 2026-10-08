using System.Diagnostics;
using System.Text.Json;

// A harmless, real child-process fixture. This is not Zapret2 or a Windows driver.
string mode = args.FirstOrDefault() ?? "wait";
if (mode == "exit") { Console.Error.WriteLine("fixture startup error"); return 17; }
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
