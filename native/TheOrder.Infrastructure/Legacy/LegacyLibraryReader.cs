using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Legacy;

public sealed class LegacyLibraryReader(IIsolatedLibrarySource source, NativeConfiguration configuration) : IReadOnlyLibrary
{
    public const int MaximumBytes = 32 * 1024 * 1024;

    public async Task<LibraryReadResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Open and parse on a worker; large JSON must not block the XAML dispatcher.
            return await Task.Run(async () =>
            {
                using var stream = source.OpenRead(configuration.DataDirectory);
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
                {
                    if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("书架文件超过 32 MiB。未读取为游戏库。");
                    buffer.Write(chunk, 0, read);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new LibraryReadResult(LibraryReadStatus.Loaded, Parse(buffer.ToArray(), cancellationToken), null);
            }, cancellationToken);
        }
        catch (DataIsolationException ex) { return LibraryReadResult.Failure(LibraryReadStatus.ConfigurationError, ex.Message); }
        catch (FileNotFoundException) { return LibraryReadResult.Failure(LibraryReadStatus.MissingFile, "指定合成资料目录中没有 library.json。未创建空库。"); }
        catch (DirectoryNotFoundException) { return LibraryReadResult.Failure(LibraryReadStatus.MissingFile, "指定的合成资料目录不存在。"); }
        catch (JsonException) { return LibraryReadResult.Failure(LibraryReadStatus.InvalidData, "JSON 已损坏、截断或结构不正确。原文件未修改，请检查合成资料。"); }
        catch (InvalidDataException ex) { return LibraryReadResult.Failure(LibraryReadStatus.InvalidData, ex.Message); }
        catch (UnauthorizedAccessException) { return LibraryReadResult.Failure(LibraryReadStatus.ReadError, "没有读取合成资料的权限。原文件未修改。"); }
        catch (IOException) { return LibraryReadResult.Failure(LibraryReadStatus.ReadError, "无法读取合成资料，文件可能正在被写入。原文件未修改。"); }
    }

    public static ImmutableArray<LibraryEntry> Parse(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken = default)
    {
        if (utf8.Length > MaximumBytes) throw new InvalidDataException("书架文件超过 32 MiB。");
        using var document = JsonDocument.Parse(utf8, new() { MaxDepth = 64 });
        LegacyValidation.Validate(document.RootElement, cancellationToken);
        var dto = document.RootElement.Deserialize<LegacyLibraryDto>(LegacyJson.Options) ?? throw new InvalidDataException("缺少书架对象。");
        var elements = document.RootElement.GetProperty("games").EnumerateArray().ToArray();
        return dto.Games.Select((g, index) => LegacyMapping.Map(g, elements[index].TryGetProperty("nonPortraitCoverDisplay", out _))).ToImmutableArray();
    }
}
