using System.Runtime.InteropServices;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class ShortcutThemeTests
{
    [Fact]
    public void ExistingShortcutChangesOnlyIconAndOnlyForExactTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "the-order-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        object? shell = null, link = null;
        try
        {
            var path = Path.Combine(root, "The Order.lnk");
            var target = Path.Combine(root, "TheOrder.exe");
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            link = ((dynamic)shell!).CreateShortcut(path);
            ((dynamic)link).TargetPath = target; ((dynamic)link).Arguments = "--test-profile example";
            ((dynamic)link).WorkingDirectory = root; ((dynamic)link).Description = "用户描述"; ((dynamic)link).Save();
            var light = Path.Combine(root, "Brand.Light.ico"); var dark = Path.Combine(root, "Brand.Dark.ico");
            Assert.False(WindowsShortcutTheme.UpdateShortcut(path, target + ".other", light));
            Assert.True(WindowsShortcutTheme.UpdateShortcut(path, target, light));
            Assert.False(WindowsShortcutTheme.UpdateShortcut(path, target, light));
            Assert.True(WindowsShortcutTheme.UpdateShortcut(path, target, dark));
            Marshal.FinalReleaseComObject(link); link = ((dynamic)shell).CreateShortcut(path);
            Assert.StartsWith(dark, (string)((dynamic)link).IconLocation);
            Assert.Equal("--test-profile example", (string)((dynamic)link).Arguments);
            Assert.Equal("用户描述", (string)((dynamic)link).Description);
            Assert.Equal(root, (string)((dynamic)link).WorkingDirectory);
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
            Directory.Delete(root, true);
        }
    }
}
