using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Application;

// Publisher-only utility, not shipped/run as part of the app. No network or installation.
if (args.Length == 4 && args[0] == "blockmap")
{
    var installer = args[1]; var version = args[2]; var output = args[3];
    if (File.Exists(output)) throw new InvalidOperationException("Use a fresh map output.");
    using var input = File.Open(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (Path.GetFileName(installer) != AppUpdateIdentity.InstallerName(version) || input.ReadByte() != 'M' || input.ReadByte() != 'Z')
        throw new InvalidOperationException("Use the exact complete Setup filename.");
    input.Position = 0;
    var map = await AppUpdateBlocks.CreateAsync(input, version, default);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(map, AppUpdateBlocks.JsonOptions);
    if (bytes.Length > AppUpdateIdentity.MaximumBlockMapBytes) throw new InvalidOperationException("Map too large.");
    using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) { file.Write(bytes); file.Flush(true); }
    Console.WriteLine(JsonSerializer.Serialize(new { blocks = map.Blocks.Length, bytes = map.Bytes, sha256 = map.Sha256,
        mapBytes = bytes.Length, mapSha256 = Convert.ToHexString(SHA256.HashData(bytes)) }));
}
else if (args.Length == 6 && args[0] == "compare")
{
    var oldInstaller = args[1]; var newInstaller = args[2]; var oldVersion = args[3]; var newVersion = args[4]; var output = args[5];
    if (File.Exists(output)) throw new InvalidOperationException("Use a fresh reconstructed output.");
    using var basis = File.Open(oldInstaller, FileMode.Open, FileAccess.Read, FileShare.Read);
    using var next = File.Open(newInstaller, FileMode.Open, FileAccess.Read, FileShare.Read);
    var currentMap = await AppUpdateBlocks.CreateAsync(basis, oldVersion, default);
    var nextMap = await AppUpdateBlocks.CreateAsync(next, newVersion, default);
    var mapBytes = JsonSerializer.SerializeToUtf8Bytes(nextMap, AppUpdateBlocks.JsonOptions).Length;
    var plan = AppUpdateBlocks.Plan(currentMap, nextMap, mapBytes);
    // Local ranges exercise exact reconstruction without executing either installer.
    using var target = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    await AppUpdateBlocks.ReconstructAsync(basis, target, plan, async (offset, count, dest, token) =>
    {
        next.Position = offset; var buffer = new byte[count]; await next.ReadExactlyAsync(buffer, token); await dest.WriteAsync(buffer, token);
    }, new Progress<AppUpdateTransfer>(), default);
    target.Position = 0; var hash = Convert.ToHexString(await SHA256.HashDataAsync(target));
    if (hash != nextMap.Sha256 || target.Length != nextMap.Bytes) throw new InvalidOperationException("Reconstructed installer differs.");
    Console.WriteLine(JsonSerializer.Serialize(new { fullBytes = nextMap.Bytes, downloadedBytes = plan.DownloadBytes + mapBytes,
        reusedBytes = plan.ReusedBytes, ranges = plan.Operations.Count(x => x.BaseOffset is null), worthwhile = plan.Worthwhile,
        reconstructedSha256 = hash, installersExecuted = false }));
}
else throw new ArgumentException("blockmap <Setup> <version> <new map> | compare <old Setup> <new Setup> <old version> <new version> <new output>");
