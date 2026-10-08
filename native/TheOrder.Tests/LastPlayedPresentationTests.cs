using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using Xunit;

namespace TheOrder.Tests;

// R6: "last played" is a read-only projection of the stored LibraryEntry.LastPlayedAt.
public sealed class LastPlayedPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(null, false, "从未游玩")]
    [InlineData("", false, "从未游玩")]
    [InlineData(null, true, "时间未知")]
    [InlineData("not-a-date", true, "时间未知")]
    [InlineData("not-a-date", false, "时间未知")]
    [InlineData("2026-10-08T05:30:00.000Z", true, "今天 13:30")]
    [InlineData("2026-10-07T05:30:00.000Z", true, "昨天")]
    [InlineData("2026-10-04T05:30:00.000Z", true, "4 天前")]
    [InlineData("2026-03-01T05:30:00.000Z", true, "3月1日")]
    [InlineData("2024-03-01T05:30:00.000Z", true, "2024-03-01")]
    [InlineData("2026-10-09T05:30:00.000Z", true, "2026-10-09")]
    public void DisplayDistinguishesNeverUnknownAndDates(string? stored, bool history, string expected)
        => Assert.Equal(expected, LastPlayedNames.Display(stored, history, Now));

    [Fact]
    public async Task ProjectionCopiesStoredValueWithoutInference()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "offline-ui-oracle.json")));
        var games = oracle.RootElement.GetProperty("games");
        var input = Encoding.UTF8.GetBytes("{\"format\":\"kun-gal-manager\",\"version\":1,\"games\":" + games.GetRawText() + "}");
        using var profile = new TransactionProfile(input);
        await using var client = new OfflineLibraryClient(new Factory(() => profile.Open(null)), new LegacyClientTextRules());
        await client.LoadAsync(default);
        var items = (await client.QueryAsync(new LibraryQuery(), default)).Items.ToDictionary(i => i.Id);
        Assert.Equal(games.GetArrayLength(), items.Count);
        foreach (var game in games.EnumerateArray())
        {
            var id = game.GetProperty("id").GetString()!;
            var stored = game.TryGetProperty("lastPlayedAt", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            Assert.Equal(stored, items[id].LastPlayedAt);
        }
        // The library file is untouched by reading.
        Assert.Equal(input, File.ReadAllBytes(profile.Main));
    }
    private sealed class Factory(Func<TransactionalLibrary> open) : ITransactionalLibraryFactory { public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation) => Task.Run(open, cancellation); }
}
