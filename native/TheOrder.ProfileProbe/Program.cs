using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;

// Child-process test host. Never a product entry, never accepts an unmarked test parent.
if (args.Length != 4) return 90;
var root = Path.GetFullPath(args[0]); var control = Path.GetDirectoryName(root)!;
if (!File.Exists(Path.Combine(control, ".profile-probe-fixture"))) return 91;
var name = args[1]; if (name != "a" && name != "b") return 92;
File.WriteAllText(Path.Combine(control, name + ".started"), "started");
try
{
    if (args[3] == "wait")
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!File.Exists(Path.Combine(control, "go"))) { if (DateTime.UtcNow > deadline) return 93; Thread.Sleep(10); }
    }
    using var profile = NativeProfileStore.Open(new(NativeProfileKind.Production, root), new NativeLibraryCodec(), new ExitProbe(args[2]));
    File.WriteAllText(Path.Combine(control, name + ".ready"), JsonSerializer.Serialize(profile.Manifest));
    if (args[3] == "wait")
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!File.Exists(Path.Combine(control, "release"))) { if (DateTime.UtcNow > deadline) return 94; Thread.Sleep(10); }
    }
    return 0;
}
catch (IOException) { return 41; }
catch (NativeProfileException) { return 42; }

sealed class ExitProbe(string fault) : IBootstrapProbe
{
    public void Reach(BootstrapPoint point)
    {
        if (point.ToString() == fault) Environment.Exit(86); // Abrupt process death: no managed finally/disposal.
    }
}
