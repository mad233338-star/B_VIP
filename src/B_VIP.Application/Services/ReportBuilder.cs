using System.Text;
using System.Text.Json;

namespace B_VIP.Application.Services
{
    public sealed record ReportCheckResult(string Name, string Status, string? Detail = null, long? DurationMs = null);

    public sealed record ReportInput(
        string Module,
        string Title,
        string? DeviceId,
        string? DeviceDescription,
        string AppVersion,
        string OsVersion,
        string? OperationId,
        string? CorrelationId,
        IReadOnlyList<ReportCheckResult> Checks,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Errors,
        IReadOnlyDictionary<string, string>? Extra);

    /// <summary>
    /// Builds diagnostic reports containing the real results of every executed check.
    /// Reports never contain passwords, tokens or API keys; <see cref="Redact"/> removes
    /// values that may be device-identifying before a report is shared.
    /// </summary>
    public static class ReportBuilder
    {
        public const string ProductName = "B_VIP Tool";
        public const string Author = "Bashir Al-Farhan";

        public static string BuildJson(ReportInput input, DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(input);

            var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["product"] = ProductName,
                ["productAuthor"] = Author,
                ["appVersion"] = input.AppVersion,
                ["os"] = input.OsVersion,
                ["generatedUtc"] = utcNow.ToString("o"),
                ["module"] = input.Module,
                ["title"] = input.Title,
                ["operationId"] = input.OperationId,
                ["correlationId"] = input.CorrelationId,
                ["device"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = input.DeviceId,
                    ["description"] = input.DeviceDescription
                },
                ["checks"] = input.Checks
                    .Select(c => (object)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = c.Name,
                        ["status"] = c.Status,
                        ["detail"] = c.Detail,
                        ["durationMs"] = c.DurationMs
                    }).ToList(),
                ["warnings"] = input.Warnings.ToList(),
                ["errors"] = input.Errors.ToList(),
                ["summary"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["total"] = input.Checks.Count,
                    ["passed"] = input.Checks.Count(c => c.Status.Equals("pass", StringComparison.OrdinalIgnoreCase)),
                    ["failed"] = input.Checks.Count(c => c.Status.Equals("fail", StringComparison.OrdinalIgnoreCase)),
                    ["skipped"] = input.Checks.Count(c => c.Status.Equals("skip", StringComparison.OrdinalIgnoreCase)),
                    ["hasErrors"] = input.Errors.Count > 0
                }
            };

            if (input.Extra is not null)
            {
                foreach ((string key, string value) in input.Extra)
                {
                    payload[$"extra.{key}"] = value;
                }
            }

            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        public static string BuildText(ReportInput input, DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(input);
            var sb = new StringBuilder();
            sb.AppendLine($"{ProductName} — Diagnostic Report");
            sb.AppendLine($"Author: {Author}");
            sb.AppendLine($"App version: {input.AppVersion}");
            sb.AppendLine($"Operating system: {input.OsVersion}");
            sb.AppendLine($"Generated (UTC): {utcNow:o}");
            sb.AppendLine($"Module: {input.Module}");
            sb.AppendLine($"Title: {input.Title}");
            sb.AppendLine($"Operation ID: {input.OperationId}");
            sb.AppendLine($"Correlation ID: {input.CorrelationId}");
            sb.AppendLine($"Device: {input.DeviceDescription ?? input.DeviceId ?? "n/a"}");
            sb.AppendLine(new string('-', 72));
            foreach (ReportCheckResult check in input.Checks)
            {
                sb.AppendLine($"[{check.Status.ToUpperInvariant()}] {check.Name}" +
                              (check.DurationMs.HasValue ? $" ({check.DurationMs} ms)" : string.Empty));
                if (!string.IsNullOrWhiteSpace(check.Detail))
                {
                    sb.AppendLine($"        {check.Detail}");
                }
            }

            if (input.Warnings.Count > 0)
            {
                sb.AppendLine("WARNINGS:");
                foreach (string warning in input.Warnings) sb.AppendLine($"  ! {warning}");
            }

            if (input.Errors.Count > 0)
            {
                sb.AppendLine("ERRORS:");
                foreach (string error in input.Errors) sb.AppendLine($"  x {error}");
            }

            if (input.Extra is not null)
            {
                foreach ((string key, string value) in input.Extra)
                {
                    sb.AppendLine($"{key}: {value}");
                }
            }

            return sb.ToString();
        }

        private static readonly string[] SensitiveKeyFragments =
        [
            "password", "passwd", "secret", "token", "apikey", "api_key", "key", "authorization",
            "credential", "imei", "serial", "mac", "email", "phone", "sn"
        ];

        /// <summary>Removes values that could identify a device or an account from a JSON report.</summary>
        public static string Redact(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement redacted = RedactElement(document.RootElement);
                return JsonSerializer.Serialize(redacted, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                return json;
            }
        }

        private static JsonElement RedactElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new Dictionary<string, JsonElement>();
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        bool sensitive = SensitiveKeyFragments.Any(f =>
                            property.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
                        obj[property.Name] = sensitive && property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)
                            ? JsonSerializer.SerializeToElement("***REDACTED***")
                            : RedactElement(property.Value);
                    }
                    return JsonSerializer.SerializeToElement(obj);
                case JsonValueKind.Array:
                    var arr = element.EnumerateArray().Select(RedactElement).ToArray();
                    return JsonSerializer.SerializeToElement(arr);
                default:
                    return element;
            }
        }
    }
}
