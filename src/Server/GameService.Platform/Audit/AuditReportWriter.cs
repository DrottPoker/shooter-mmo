using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mmorpg.GameService.Platform.Audit;

// The two forms of an audit report: readable text for an operator and JSON for tools.
public static class AuditReportWriter
{
    public static string ToText(AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Economy audit of the snapshot at {FormatTime(report.SnapshotAt)}: ")
            .Append(CultureInfo.InvariantCulture, $"{report.Checks.Count} checks, {report.Violations} violations, ")
            .AppendLine(CultureInfo.InvariantCulture, $"{report.Duration.TotalMilliseconds:0} ms.");
        foreach (var check in report.Checks)
        {
            var state = !check.Ran ? "FAILED" : check.Violations > 0 ? "VIOLATED" : "ok";
            text.Append(CultureInfo.InvariantCulture, $"  {state,-9} {check.Id}: ");
            text.AppendLine(check.Ran
                ? string.Create(CultureInfo.InvariantCulture, $"{check.Examined} examined, {check.Violations} violations, {check.Duration.TotalMilliseconds:0} ms")
                : string.Create(CultureInfo.InvariantCulture, $"{check.Error}, {check.Duration.TotalMilliseconds:0} ms"));
            if (check.Violations > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"            {check.Description}");
                foreach (var example in check.Examples)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"            {FormatExample(example)}");
                }
            }
        }

        text.AppendLine(Verdict(report));
        return text.ToString();
    }

    public static string ToJson(AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("outcome", OutcomeName(report.Outcome));
            json.WriteNumber("exitCode", report.ExitCode);
            if (report.SnapshotAt is { } snapshotAt)
            {
                json.WriteString("snapshotAt", snapshotAt);
            }
            else
            {
                json.WriteNull("snapshotAt");
            }

            json.WriteNumber("durationMilliseconds", Math.Round(report.Duration.TotalMilliseconds, 1));
            json.WriteNumber("violations", report.Violations);
            WriteNullableString(json, "error", report.Error);
            json.WriteStartArray("checks");
            foreach (var check in report.Checks)
            {
                json.WriteStartObject();
                json.WriteString("id", check.Id);
                json.WriteString("description", check.Description);
                json.WriteBoolean("ran", check.Ran);
                json.WriteNumber("examined", check.Examined);
                json.WriteNumber("violations", check.Violations);
                json.WriteNumber("durationMilliseconds", Math.Round(check.Duration.TotalMilliseconds, 1));
                WriteNullableString(json, "error", check.Error);
                json.WriteStartArray("examples");
                foreach (var example in check.Examples)
                {
                    json.WriteStartObject();
                    foreach (var field in example)
                    {
                        WriteNullableString(json, field.Key, field.Value);
                    }

                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The fields of an example as name=value pairs, in the order the check returned them.
    public static string FormatExample(IReadOnlyDictionary<string, string?> example)
    {
        ArgumentNullException.ThrowIfNull(example);
        return string.Join(", ", example.Select(field => $"{field.Key}={field.Value ?? "null"}"));
    }

    public static string OutcomeName(AuditOutcome outcome)
    {
        return outcome switch
        {
            AuditOutcome.Clean => "clean",
            AuditOutcome.Violations => "violations",
            _ => "failed"
        };
    }

    private static string Verdict(AuditReport report)
    {
        var failed = report.FailedChecks.Select(check => check.Id).ToArray();
        var notRun = report.Error is null
            ? failed.Length == 0 ? null : $"{failed.Length} checks could not run: {string.Join(", ", failed)}."
            : $"The audit could not run every check: {report.Error}";
        return report.Outcome switch
        {
            AuditOutcome.Clean => "The economy is consistent.",
            AuditOutcome.Violations =>
                $"Violations found by {report.ViolatedChecks.Count} checks: {string.Join(", ", report.ViolatedChecks.Select(check => check.Id))}."
                + (notRun is null ? string.Empty : " " + notRun),
            _ => notRun ?? "The audit could not run."
        };
    }

    private static string FormatTime(DateTimeOffset? time)
    {
        return time is { } value
            ? value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : "none";
    }

    private static void WriteNullableString(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }
}
