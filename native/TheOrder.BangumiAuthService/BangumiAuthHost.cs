using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using TheOrder.Application;

namespace TheOrder.BangumiAuthService;

public static class BangumiAuthHost
{
    public static WebApplication Build(WebApplicationBuilder builder, BangumiAuthSessions sessions)
    {
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 160, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch { if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsync("请求未完成。"); } }
        });
        app.UseRateLimiter();
        foreach (var path in new[] { BangumiOAuthProtocol.Begin, BangumiOAuthProtocol.Poll, BangumiOAuthProtocol.Cancel, BangumiOAuthProtocol.Refresh })
        {
            var route = path;
            app.MapPost("/" + route, async context =>
            {
                if (context.Request.QueryString.HasValue || context.Request.ContentType?.Split(';')[0] != "application/json")
                { context.Response.StatusCode = 400; return; }
                var response = await sessions.PostAsync(route, context.Request.Body, context.RequestAborted);
                context.Response.StatusCode = response.Status;
                context.Response.ContentType = "application/json";
                if (response.Body is { } bytes)
                {
                    try { await context.Response.Body.WriteAsync(bytes, context.RequestAborted); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                }
            });
        }
        app.MapGet("/" + BangumiOAuthProtocol.Callback, async context =>
        {
            var query = context.Request.Query;
            if (query.Count is < 1 or > 4 || query.Any(x => x.Value.Count != 1 || x.Key is not ("state" or "code" or "error" or "error_description")))
            { context.Response.StatusCode = 400; return; }
            var status = await sessions.CallbackAsync(query["state"].ToString(), query["code"].ToString(), query.ContainsKey("error"), context.RequestAborted);
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(status == 200 ? "授权处理完成。请返回 The Order；此页面可以关闭。" : "授权未完成，请返回 The Order 重试。");
        });
        app.MapGet("/health", () => Results.Json(new { service = "TheOrder.BangumiAuthService", version = 1, ready = true }));
        app.Lifetime.ApplicationStopped.Register(sessions.Dispose);
        return app;
    }
}
