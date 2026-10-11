using System.Runtime.InteropServices;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class IsolationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "the-order-native-test-" + Guid.NewGuid().ToString("N"));
    public IsolationTests() { Directory.CreateDirectory(directory); }
    private void Mark() => File.WriteAllText(Path.Combine(directory, IsolatedLibrarySource.MarkerName), IsolatedLibrarySource.MarkerValue);
    private void Library() => File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures/normal/library.json"), Path.Combine(directory, "library.json"));
    private Task<LibraryReadResult> Read() => new LegacyLibraryReader(new IsolatedLibrarySource(), new(directory)).LoadAsync(default);

    [Fact] public async Task UnknownDirectoryCannotBeRead()
    {
        Library(); Assert.Equal(LibraryReadStatus.ConfigurationError, (await Read()).Status);
    }
    [Fact] public async Task InvalidMarkerCannotBeRead()
    {
        Library(); File.WriteAllText(Path.Combine(directory, IsolatedLibrarySource.MarkerName), "not-synthetic");
        Assert.Equal(LibraryReadStatus.ConfigurationError, (await Read()).Status);
    }
    [Fact] public async Task MissingLibraryDoesNotCreateOne()
    {
        Mark(); Assert.Equal(LibraryReadStatus.MissingFile, (await Read()).Status);
        Assert.False(File.Exists(Path.Combine(directory, "library.json")));
    }
    [Fact] public async Task FileBeingWrittenIsNotAnEmptyLibrary()
    {
        Mark(); Library(); using var writer = new FileStream(Path.Combine(directory, "library.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(LibraryReadStatus.ReadError, (await Read()).Status);
    }
    [Fact] public async Task HardLinkedLibraryIsRejected()
    {
        Mark(); Library();
        Assert.True(CreateHardLink(Path.Combine(directory, "linked.json"), Path.Combine(directory, "library.json"), IntPtr.Zero));
        Assert.Equal(LibraryReadStatus.ConfigurationError, (await Read()).Status);
    }
    [Fact] public async Task SuccessfulReadCreatesNoSettingsBackupsOrCache()
    {
        Mark(); Library(); var before = Directory.GetFiles(directory).Order().ToArray();
        Assert.True((await Read()).IsSuccess); Assert.Equal(before, Directory.GetFiles(directory).Order().ToArray());
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(full).StartsWith("the-order-native-test-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test directory");
        Directory.Delete(full, true);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr securityAttributes);
}
