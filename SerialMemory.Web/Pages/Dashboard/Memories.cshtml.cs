using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public partial class MemoriesModel(AppConfig appConfig) : DashboardPageModel(appConfig)
{
    public void OnGet()
    {
        // All properties come from DashboardPageModel base class
    }

    /// <summary>
    /// Formats memory content by detecting code type and wrapping in markdown fences.
    /// Returns formatted markdown suitable for rendering with syntax highlighting.
    /// </summary>
    public static string FormatMemoryContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        // If already contains fenced code blocks, return as-is
        if (content.Contains("```"))
            return content;

        var detectedLanguage = DetectLanguage(content);

        // If plain text or no code detected, return as-is
        if (detectedLanguage == "text")
            return content;

        // Wrap in appropriate code fence
        return $"```{detectedLanguage}\n{content.Trim()}\n```";
    }

    /// <summary>
    /// Detects the programming language of the content.
    /// </summary>
    public static string DetectLanguage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "text";

        var trimmed = content.Trim();

        // Check for JSON (starts with { or [)
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
            (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                JsonDocument.Parse(trimmed);
                return "json";
            }
            catch
            {
                // Not valid JSON, continue detection
            }
        }

        // Check for XML/HTML (starts with <)
        if (trimmed.StartsWith('<') && XmlTagRegex().IsMatch(trimmed))
        {
            return trimmed.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase)
                ? "html"
                : "xml";
        }

        // Check for YAML (key: value patterns, no semicolons, indentation-based)
        if (YamlPatternRegex().IsMatch(trimmed) &&
            !trimmed.Contains(';') &&
            !trimmed.Contains('{'))
        {
            return "yaml";
        }

        // Check for SQL keywords
        if (SqlKeywordRegex().IsMatch(trimmed))
        {
            return "sql";
        }

        // Check for C# patterns
        if (IsCSharpCode(trimmed))
        {
            return "csharp";
        }

        // Check for JavaScript/TypeScript patterns
        if (JavaScriptPatternRegex().IsMatch(trimmed))
        {
            return "javascript";
        }

        // Check for Python patterns
        if (PythonPatternRegex().IsMatch(trimmed))
        {
            return "python";
        }

        // Check for Bash/Shell patterns
        if (BashPatternRegex().IsMatch(trimmed))
        {
            return "bash";
        }

        // Default to text for prose/mixed content
        return "text";
    }

    private static bool IsCSharpCode(string content)
    {
        // Strong C# indicators
        var strongIndicators = new[]
        {
            "builder.Services",
            "IServiceCollection",
            "AddScoped",
            "AddSingleton",
            "AddTransient",
            "namespace ",
            "using System",
            "public class ",
            "private class ",
            "internal class ",
            "public sealed class ",
            "public interface ",
            "async Task",
            "=> ",
            ".ConfigureAwait(",
            "var ",
            "ILogger<",
            "IConfiguration",
            "[Authorize]",
            "[HttpGet]",
            "[HttpPost]",
            "public record ",
            "public enum "
        };

        foreach (var indicator in strongIndicators)
        {
            if (content.Contains(indicator, StringComparison.Ordinal))
                return true;
        }

        // Semicolon-heavy pattern (C#/Java style)
        var semicolonCount = content.Count(c => c == ';');
        var lineCount = content.Split('\n').Length;
        if (lineCount > 2 && semicolonCount >= lineCount / 2)
        {
            // Has braces too - likely C-style language
            if (content.Contains('{') && content.Contains('}'))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Formats JSON content with pretty-printing.
    /// </summary>
    public static string FormatJson(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    // Generated regex patterns for performance
    [GeneratedRegex(@"^<[a-zA-Z]", RegexOptions.Compiled)]
    private static partial Regex XmlTagRegex();

    [GeneratedRegex(@"^\s*[\w-]+\s*:\s*\S", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex YamlPatternRegex();

    [GeneratedRegex(@"\b(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|FROM|WHERE|JOIN|GROUP BY|ORDER BY)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SqlKeywordRegex();

    [GeneratedRegex(@"\b(function|const|let|var|=>|import|export|require\(|module\.exports)\b", RegexOptions.Compiled)]
    private static partial Regex JavaScriptPatternRegex();

    [GeneratedRegex(@"(^def |^class |^import |^from .+ import|if __name__|print\(|self\.|async def )", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex PythonPatternRegex();

    [GeneratedRegex(@"(^#!/bin/|^\s*\$\s+|^echo |^export |^source |^alias |\|\s*grep|\|\s*awk|\|\s*sed)", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex BashPatternRegex();
}
