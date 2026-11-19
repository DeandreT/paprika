using Microsoft.Net.Http.Headers;

namespace paprika;

internal static class PreviewEngineAssets
{
    private const string Prefix = "/assets/preview-engine";

    public static void UsePreviewEngineAssets(this WebApplication app) =>
        app.Use(
            async (context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments(Prefix, out var rest))
                {
                    await next(context);
                    return;
                }

                // Own the entire prefix: a miss must not expose a sidecar through the
                // ordinary static-file handler or return index.html from the SPA fallback.
                var response = context.Response;
                response.StatusCode = StatusCodes.Status404NotFound;
                response.Headers.CacheControl = "no-store";
                if (
                    !HttpMethods.IsGet(context.Request.Method)
                    && !HttpMethods.IsHead(context.Request.Method)
                )
                {
                    response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    response.Headers.Allow = "GET, HEAD";
                    return;
                }

                var parts = (rest.Value ?? "").Split('/');
                if (parts.Length < 3 || parts.Skip(1).Any(p => !SafeSegment(p)))
                    return;
                var mime = Path.GetExtension(parts[^1]).ToLowerInvariant() switch
                {
                    ".js" or ".mjs" => "text/javascript",
                    ".wasm" => "application/wasm",
                    ".bin" or ".cff" or ".pfb" or ".bcmap" or ".icc" => "application/octet-stream",
                    ".ttf" => "font/ttf",
                    ".otf" => "font/otf",
                    ".woff" => "font/woff",
                    ".woff2" => "font/woff2",
                    _ => null,
                };
                if (mime is null || app.Environment.WebRootPath is not { } root)
                    return;
                var directory = root;
                foreach (var name in new[] { "assets", "preview-engine" }.Concat(parts[1..^1]))
                {
                    directory = Path.Combine(directory, name);
                    var info = new DirectoryInfo(directory);
                    if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return;
                }

                var requested = context.Request.GetTypedHeaders().AcceptEncoding;
                var path = Path.Combine(directory, parts[^1]);
                response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
                foreach (
                    var (encoding, suffix) in Representations.OrderByDescending(r =>
                        Quality(requested, r.Encoding)
                    )
                )
                {
                    var file = new FileInfo(path + suffix);
                    if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    if (Quality(requested, encoding) <= 0)
                    {
                        response.StatusCode = StatusCodes.Status406NotAcceptable;
                        return;
                    }

                    response.StatusCode = StatusCodes.Status200OK;
                    if (encoding != "identity")
                        response.Headers.ContentEncoding = encoding;
                    response.Headers.CacheControl = "public, max-age=31536000, immutable";
                    response.Headers.XContentTypeOptions = "nosniff";
                    await Results.File(file.FullName, mime).ExecuteAsync(context);
                    return;
                }
            }
        );

    private static bool SafeSegment(string segment) =>
        segment.Length is > 0 and <= 255
        && (char.IsAsciiLetterOrDigit(segment[0]) || segment[0] is '-' or '_')
        && segment[^1] is not ('.' or ' ')
        && segment.All(c =>
            char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' or '(' or ')' or '+'
        );

    // Equal preferences favor Brotli, then gzip, then identity. An unspecified
    // identity is a fallback unless explicitly forbidden (including by *;q=0).
    private static readonly (string Encoding, string Suffix)[] Representations =
    [
        ("br", ".br"),
        ("gzip", ".gz"),
        ("identity", ""),
    ];

    private static double Quality(IList<StringWithQualityHeaderValue> values, string encoding)
    {
        double? Get(string name) =>
            values.FirstOrDefault(v => v.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                is { } header
                ? header.Quality ?? 1
                : null;
        if (Get(encoding) is { } explicitQuality)
            return explicitQuality;
        var wildcard = Get("*");
        if (encoding == "identity")
            return wildcard == 0 ? 0 : double.Epsilon;
        return wildcard ?? 0;
    }
}
