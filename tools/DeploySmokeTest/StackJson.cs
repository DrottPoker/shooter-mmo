using System.Text.Json;

namespace Mmorpg.Tools.DeploySmokeTest;

// Reads the few fields of GameService's answers that the checks look at, tolerating a body that is
// no JSON at all, which is what a proxy without an upstream answers.
public static class StackJson
{
    public static string? Text(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Every dependency that the readiness answer lists is reachable, and at least one is listed.
    public static bool DependenciesReachable(string body, out string names)
    {
        names = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Array
                || dependencies.GetArrayLength() == 0)
            {
                return false;
            }

            names = string.Join(", ", dependencies.EnumerateArray().Select(item => item.GetProperty("name").GetString()));
            return dependencies.EnumerateArray().All(item => item.GetProperty("isReachable").GetBoolean());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}
