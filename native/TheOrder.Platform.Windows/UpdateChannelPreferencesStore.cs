using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class UpdateChannelPreferencesStore(NativeConfiguration configuration) : IAppUpdateChannelStore
{
    public const string FileName = "native-update-channel-v1.json";
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, AppUpdateChannel Channel);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly SemaphoreSlim queue = new(1, 1);
    private Envelope? Read(NativeProfileStore profile)
    {
        var path = Path.Combine(profile.Root, FileName);
        try
        {
            using var file = IsolatedLibrarySource.OpenChecked(path);
            if (file.Length > 4096) throw new InvalidDataException("更新通道偏好无效，未更改原文件。");
            var value = JsonSerializer.Deserialize<Envelope>(file, Options);
            if (value is null || value.Version != 1 || value.ProfileId != profile.Manifest.ProfileId || value.StoreId != profile.Manifest.StoreId || !Enum.IsDefined(value.Channel))
                throw new InvalidDataException("更新通道偏好不属于当前资料或版本不受支持。");
            return value;
        }
        catch (FileNotFoundException) { return null; }
    }
    public async Task<AppUpdateChannel?> LoadAsync(CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var profile = Required(); var held = IsolatedLibraryPersistence.PinDirectoryChain(profile.Root);
            try { profile.ValidateAccess(); return Read(profile)?.Channel; }
            finally { foreach (var handle in held) handle.Dispose(); }
        }
        finally { queue.Release(); }
    }
    public async Task SaveAsync(AppUpdateChannel channel, CancellationToken cancellation)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var profile = Required(); var held = IsolatedLibraryPersistence.PinDirectoryChain(profile.Root);
            try
            {
                profile.ValidateAccess(); _ = Read(profile); cancellation.ThrowIfCancellationRequested();
                await WindowsAtomicFile.WriteAsync(Path.Combine(profile.Root, FileName), JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, profile.Manifest.ProfileId, profile.Manifest.StoreId, channel))).ConfigureAwait(false);
            }
            finally { foreach (var handle in held) handle.Dispose(); }
        }
        finally { queue.Release(); }
    }
    private NativeProfileStore Required()
    {
        _ = NativeProfileAccess.Validate(configuration);
        if (configuration.Profile is not NativeProfileStore profile) throw new AppUpdateException("当前资料无法保存更新通道。");
        profile.ValidateAccess(); return profile;
    }
}
