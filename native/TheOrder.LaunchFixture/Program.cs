using System.Diagnostics;
using System.Text.Json;

// Project-owned deterministic fixture. Never copied into a real game directory.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length < 3) return;
        var root = Path.GetFullPath(args[0]); var mode = args[1]; var name = args[2];
        if (!File.Exists(Path.Combine(root, ".the-order-launch-fixture"))) return;
        var stopFile = Path.Combine(root, name + ".stop");
        File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(new
        { pid = Environment.ProcessId, cwd = Environment.CurrentDirectory, arguments = args.Skip(3).ToArray(), commandLine = System.Runtime.InteropServices.Marshal.PtrToStringUni(GetCommandLine()), mode, started = DateTimeOffset.UtcNow }));
        if (mode == "empty") return;
        if (mode is "launcher" or "chain" or "helper" or "multiple" or "quick-launcher")
        {
            Thread.Sleep(600); // Lets the bounded observer establish this parent's identity.
            if (mode == "chain") Spawn("helper.exe", "helper", name + "-helper");
            else
            {
                Spawn("child_game.exe", mode == "quick-launcher" ? "quick" : "game", name + "-game");
                if (mode == "multiple") Spawn("second_game.exe", "game", name + "-other");
            }
            Thread.Sleep(700); return;
        }
        System.Windows.Forms.Application.EnableVisualStyles();
        using var form = new Form { Text = "The Order · synthetic process " + name, Width = 420, Height = 220 };
        form.Controls.Add(new Label { Text = "Synthetic launch fixture\nPID: " + Environment.ProcessId, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => { if (mode == "quick" || File.Exists(stopFile)) form.Close(); };
        timer.Start();
        System.Windows.Forms.Application.Run(form);
        return;
        void Spawn(string executable, string childMode, string childName)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, executable)) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            start.ArgumentList.Add(root); start.ArgumentList.Add(childMode); start.ArgumentList.Add(childName);
            using var child = Process.Start(start);
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetCommandLineW")]
    private static extern IntPtr GetCommandLine();
}
