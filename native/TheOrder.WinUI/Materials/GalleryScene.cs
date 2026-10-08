namespace TheOrder.WinUI.Materials;

// The same finite Gallery content, expressed as drawing commands. This is not
// arbitrary XAML capture or a screenshot cache. Each requested revision is drawn.
internal sealed record GalleryScene(uint Background, IReadOnlyList<GalleryPrimitive> Primitives)
{
    internal const int Width = 1300, Height = 660;
    internal static GalleryScene Create(int mode, string? reference, string? covers)
    {
        if (mode is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(mode));
        var items = new List<GalleryPrimitive>();
        void Rect(float x, float y, float w, float h, uint color) => items.Add(new(0, x, y, w, h, color));
        void Image(string path, float x, float y, float w, float h, bool fill = false)
        {
            var full = Path.GetFullPath(path);
            if (!full.Contains(Path.DirectorySeparatorChar + ".artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                throw new InvalidDataException("Existing isolated Test artwork required");
            items.Add(new(2, x, y, w, h, 0, full, fill ? 1 : 0));
        }
        switch (mode)
        {
            case 0:
                for (var row = 0; row < 8; row++)
                    items.Add(new(1, 10 + row % 2 * 30, 14 + row * 46, row % 2 == 0 ? 24 : 16, 0, 0xff141619,
                        "GLASS  光学边缘  ガラス  0123456789  —  The Order", 600));
                for (var i = 0; i < 18; i++) Rect(18 + i * 61, 0, i % 2 == 0 ? 2 : 1, Height, 0xff181a1d);
                break;
            case 1:
                for (var row = 0; row < 11; row++)
                    for (var column = 0; column < 22; column++)
                        Rect(column * 64, row * 64, 64, 64, (row + column) % 2 == 0 ? 0xffdfeaed : 0xfff4f6ee);
                Rect(74, 40, 116, 224, 0xff4d899f); Rect(348, 164, 184, 128, 0xffcc8959); Rect(704, 56, 154, 192, 0xff83a58d);
                for (var i = 0; i < 26; i++) Rect(18 + i * 48, 0, 2, 680, 0x96214b5b);
                for (var i = 0; i < 12; i++) Rect(0, 18 + i * 48, Width, 1, 0xb42c444e);
                break;
            case 2:
                if (covers is null) throw new InvalidDataException("Explicit Test covers required");
                for (var i = 0; i < 6; i++) Image(Path.Combine(covers, $"cover-{i * 3:D5}.jpg"), 24 + i * 215, -10, 200, 300, true);
                break;
            case 5:
                if (reference is null) throw new InvalidDataException("Explicit Test reference required");
                Image(reference, -65, -866, 1300, 2826); break;
            case 6: case 7:
                if (reference is null) throw new InvalidDataException("Explicit Test artwork directory required");
                Image(Path.Combine(Path.GetDirectoryName(reference)!, mode == 6 ? "steam-1230140.jpg" : "steam-1094820.jpg"), 0, -430, 1300, 1950); break;
        }
        return new(mode == 4 ? 0xff1f242b : 0xffedeff0, items);
    }
}

internal sealed record GalleryPrimitive(int Kind, float X, float Y, float Width, float Height,
    uint Color, string? Content = null, int Option = 0);
