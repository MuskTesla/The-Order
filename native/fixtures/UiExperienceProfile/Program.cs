using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;

// Only the frozen synthetic payload is allowed; no source profile or credentials
// are copied. NativeProfileStore generates a fresh identity and valid envelopes.
if (args.Length != 2) throw new ArgumentException("Expected synthetic seed and fresh Test output.");
var seed = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
if (root is null) throw new InvalidOperationException("Workspace root not found.");
var artifacts = Path.Combine(root.FullName, ".artifacts", "local-development-integration") + Path.DirectorySeparatorChar;
if (!seed.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || !output.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase)
    || Directory.Exists(output)) throw new InvalidOperationException("Fresh isolated Test artifact paths required.");
using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(seed, "seed.json")));
if (manifest.RootElement.GetProperty("ProfileKind").GetString() != "Test") throw new InvalidOperationException("Test seed required.");
var data = File.ReadAllBytes(Path.Combine(seed, "library.payload.json"));
if (Convert.ToHexString(SHA256.HashData(data)) != "351057C00FA338040565CFFC821AC9612757BA1D43E99B0ED4A0CF37AE883DBB")
    throw new InvalidOperationException("Frozen synthetic library identity mismatch.");
var ui = File.ReadAllBytes(Path.Combine(seed, "native-ui.payload.json"));
if (Convert.ToHexString(SHA256.HashData(ui)) != manifest.RootElement.GetProperty("uiSha256").GetString())
    throw new InvalidOperationException("Synthetic UI seed identity mismatch.");
using var store = NativeProfileStore.Open(new NativeProfileRequest(NativeProfileKind.Test, output), new NativeLibraryCodec());
var writer = store.CreateLibraryPersistence();
try { await writer.SaveAsync(writer.ReadMain()!, data); }
finally { (writer as IDisposable)?.Dispose(); }
store.WriteComponent("native-ui.json", ui);
store.CheckpointBackups();
Console.WriteLine(JsonSerializer.Serialize(new { store.Manifest.ProfileId, store.Manifest.ProfileKind, store.Root, credentialsCopied = false }));
