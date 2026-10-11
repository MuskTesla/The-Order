using System.Net;
using TheOrder.Application;
using TheOrder.Platform.Windows;

namespace TheOrder.WinUI.Startup;

internal sealed class PerformanceImageFixture : IDisposable
{
    public HttpClient Client { get; }
    public PerformanceImageFixture()
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_PERFORMANCE_COVERS") ?? throw new InvalidOperationException("Cover fixture required"));
        if (Program.Profile?.Manifest.ProfileKind != NativeProfileKind.Test || !root.Contains(Path.DirectorySeparatorChar + ".artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Isolated Test media required");
        NativeProfilePaths.Validate(new(NativeProfileKind.Test, root)); Client = new(new Handler(root));
    }
    public void Dispose() => Client.Dispose();
    private sealed class Handler(string root) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            string file = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.Host != "images.example" || !System.Text.RegularExpressions.Regex.IsMatch(file, "^cover-[0-9]{5}\\.(jpg|png|webp)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return new(HttpStatusCode.NotFound);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(root, file), cancellation)) };
            response.Content.Headers.ContentType = new(Path.GetExtension(file) switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" }); return response;
        }
    }
}
