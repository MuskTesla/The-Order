namespace TheOrder.BangumiAuthService;

internal static class ServiceEntryPoint
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        var options = new BangumiAuthOptions(builder.Configuration["Bangumi:AppId"] ?? "",
            builder.Configuration["Bangumi:ClientSecret"] ?? "", builder.Configuration["Bangumi:ServiceUrl"] ?? "");
        using var network = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        await BangumiAuthHost.Build(builder, new BangumiAuthSessions(options, network)).RunAsync();
    }
}
