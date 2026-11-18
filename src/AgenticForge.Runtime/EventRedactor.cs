using System.Text.RegularExpressions;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

/// <summary>
/// Data-minimization rules applied to every event before it reaches a sink. Events never include step input/output;
/// the only free text that can enter them is failure messages/details and attributes, which are scrubbed here.
/// </summary>
public static partial class EventRedactor
{
    public const string Mask = "[REDACTED]";
    public const int MaxTextLength = 500;

    // Well-known token shapes, bearer credentials, and key=value / "key": "value" pairs with sensitive key names.
    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{16,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,})")]
    private static partial Regex TokenShape();

    [GeneratedRegex(@"(?i)(""?[\w.-]*(?:password|passwd|secret|token|api[_-]?key|apikey|authorization|credential|private[_-]?key)[\w.-]*""?\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&]+)")]
    private static partial Regex SensitivePair();

    [GeneratedRegex(@"(?i)password|passwd|secret|token|api[_-]?key|apikey|authorization|credential|private[_-]?key|prompt")]
    private static partial Regex SensitiveName();

    public static string Redact(string text)
    {
        var t = AuthHeader().Replace(text, m => m.Groups[1].Value + " " + Mask);
        t = TokenShape().Replace(t, Mask);
        t = SensitivePair().Replace(t, m => m.Groups[1].Value + Mask);
        return t.Length > MaxTextLength ? t[..MaxTextLength] + "..." : t;
    }

    public static Failure? Redact(Failure? failure)
        => failure is null ? null : failure with
        {
            Message = Redact(failure.Message),
            Details = Redact(failure.Details),
        };

    /// <summary>Values of attributes whose name suggests a secret or prompt are masked outright; others are scrubbed.</summary>
    public static IReadOnlyDictionary<string, string>? Redact(IReadOnlyDictionary<string, string>? values)
        => values?.ToDictionary(kv => kv.Key, kv => SensitiveName().IsMatch(kv.Key) ? Mask : Redact(kv.Value));
}
