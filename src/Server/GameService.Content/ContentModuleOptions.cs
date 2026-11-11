using Mmorpg.GameService.Platform.Configuration;

namespace Mmorpg.GameService.Content;

// The content files GameService serves and how. The host passes the catalog paths that
// the Items and Quests modules load, so both serve and run exactly the same files.
public sealed record ContentModuleOptions(
    string ItemCatalogPath,
    string QuestCatalogPath,
    string TextTablesPath,
    string? BaseUrl,
    int RateLimitPermitLimit,
    TimeSpan RateLimitWindow)
{
    public const string DefaultTextTablesPath = "WorldData/Text/en";
    public const int DefaultRateLimitPermitLimit = 120;
    public const int DefaultRateLimitWindowSeconds = 60;

    private const string TextTablesPathKey = "Content:TextTablesPath";
    private const string BaseUrlKey = "Content:BaseUrl";
    private const string PermitLimitKey = "RateLimiting:Content:PermitLimit";
    private const string WindowSecondsKey = "RateLimiting:Content:WindowSeconds";

    public static ContentModuleOptions FromConfiguration(
        IConfiguration configuration,
        string itemCatalogPath,
        string questCatalogPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemCatalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(questCatalogPath);

        var errors = new List<string>();
        var configuredPath = configuration[TextTablesPathKey];
        var textTablesPath = ResolvePath(
            string.IsNullOrWhiteSpace(configuredPath) ? DefaultTextTablesPath : configuredPath);
        var baseUrl = ReadBaseUrl(configuration[BaseUrlKey], errors);
        var permitLimit = ReadOptionalIntRange(
            configuration,
            PermitLimitKey,
            DefaultRateLimitPermitLimit,
            1,
            10_000,
            errors);
        var windowSeconds = ReadOptionalIntRange(
            configuration,
            WindowSecondsKey,
            DefaultRateLimitWindowSeconds,
            1,
            3_600,
            errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "GameService content configuration is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new ContentModuleOptions(
            itemCatalogPath,
            questCatalogPath,
            textTablesPath,
            baseUrl,
            permitLimit,
            TimeSpan.FromSeconds(windowSeconds));
    }

    private static string ResolvePath(string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path));
    }

    // An absolute http or https URL without query or fragment; the trailing slash is dropped.
    private static string? ReadBaseUrl(string? value, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add($"{BaseUrlKey} must be an absolute http or https URL without query or fragment.");
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static int ReadOptionalIntRange(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum,
        ICollection<string> errors)
    {
        return string.IsNullOrWhiteSpace(configuration[key])
            ? defaultValue
            : ConfigurationValidation.ValidateIntRange(configuration, key, minimum, maximum, errors);
    }
}
