using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Content;

// Anonymous, because the files ship inside every client and the client fetches them
// before login. Every route shares the per-address content rate limit.
internal static class ContentEndpoints
{
    public const string NotFoundCode = "content_not_found";

    private const string JsonContentType = "application/json";
    private const string ManifestCacheControl = "no-cache";
    private const string FileCacheControl = "public, max-age=31536000, immutable";

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ContentFileStore.RoutePrefix, GetManifest)
            .AllowAnonymous()
            .RequireRateLimiting(ContentRateLimiterPolicy.PolicyName);

        app.MapGet(ContentFileStore.RoutePrefix + "/{kind}/{id}/{revision}", GetCatalogFile)
            .AllowAnonymous()
            .RequireRateLimiting(ContentRateLimiterPolicy.PolicyName);

        app.MapGet(ContentFileStore.RoutePrefix + "/{kind}/{locale}/{id}/{revision}", GetTextTableFile)
            .AllowAnonymous()
            .RequireRateLimiting(ContentRateLimiterPolicy.PolicyName);

        return app;
    }

    private static IResult GetManifest(HttpContext httpContext, ContentFileStore store)
    {
        return Serve(httpContext, store.ManifestBytes, store.ManifestETag, ManifestCacheControl);
    }

    private static IResult GetCatalogFile(
        HttpContext httpContext,
        ContentFileStore store,
        string kind,
        string id,
        string revision)
    {
        return ServeFile(httpContext, store, kind, null, id, revision);
    }

    private static IResult GetTextTableFile(
        HttpContext httpContext,
        ContentFileStore store,
        string kind,
        string locale,
        string id,
        string revision)
    {
        return ServeFile(httpContext, store, kind, locale, id, revision);
    }

    private static IResult ServeFile(
        HttpContext httpContext,
        ContentFileStore store,
        string kind,
        string? locale,
        string id,
        string revision)
    {
        // The route values are checked before the lookup, and every miss looks the same.
        var relativePath = ContentPaths.TryCreateRelativePath(kind, locale, id, revision);
        var file = relativePath is null ? null : store.Find(relativePath);
        if (file is null)
        {
            return ServiceResult<object>.NotFound(
                    NotFoundCode,
                    "No content file has this kind, id, and revision.")
                .ToHttpResult();
        }

        return Serve(httpContext, file.Bytes, $"\"{file.Revision}\"", FileCacheControl);
    }

    private static IResult Serve(
        HttpContext httpContext,
        byte[] bytes,
        string etag,
        string cacheControl)
    {
        httpContext.Response.Headers.ETag = etag;
        httpContext.Response.Headers.CacheControl = cacheControl;
        var requestEtags = httpContext.Request.Headers.IfNoneMatch.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requestEtags.Any(value => MatchesEtag(value, etag)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Bytes(bytes, JsonContentType);
    }

    // If-None-Match uses the weak comparison, so a weak tag of the same value matches too.
    private static bool MatchesEtag(string requestEtag, string currentEtag)
    {
        if (string.Equals(requestEtag, "*", StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = requestEtag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
            ? requestEtag[2..].TrimStart()
            : requestEtag;
        return string.Equals(normalized, currentEtag, StringComparison.Ordinal);
    }
}
