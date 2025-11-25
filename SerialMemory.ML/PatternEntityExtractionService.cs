using SerialMemory.Core.Interfaces;
using System.Text.RegularExpressions;

namespace SerialMemory.ML;

/// <summary>
/// Pattern-based entity extraction using regex
/// Can be extended with ML-based NER or external API calls
/// </summary>
public partial class PatternEntityExtractionService : IEntityExtractionService
{
    [GeneratedRegex(@"\b[A-Z][a-z]+ [A-Z][a-z]+\b", RegexOptions.Compiled)]
    private static partial Regex PersonNameRegex();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?: [A-Z][a-z]+)* (?:Inc|Corp|LLC|Ltd|Company|Corporation)\b", RegexOptions.Compiled)]
    private static partial Regex OrganizationRegex();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?: [A-Z][a-z]+)*,? [A-Z]{2}\b", RegexOptions.Compiled)]
    private static partial Regex LocationRegex();

    [GeneratedRegex(@"\b\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b(?:CEO|CTO|CFO|COO|Manager|Director|President|VP|Engineer|Developer|Designer|Analyst)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    public Task<List<ExtractedEntity>> ExtractEntitiesAsync(string text, CancellationToken cancellationToken = default)
    {
        var entities = new List<ExtractedEntity>();

        // Extract emails
        foreach (Match match in EmailRegex().Matches(text))
        {
            entities.Add(new ExtractedEntity(
                match.Value,
                "EMAIL",
                match.Index,
                match.Index + match.Length,
                1.0f
            ));
        }

        // Extract URLs
        foreach (Match match in UrlRegex().Matches(text))
        {
            entities.Add(new ExtractedEntity(
                match.Value,
                "URL",
                match.Index,
                match.Index + match.Length,
                1.0f
            ));
        }

        // Extract organizations (must come before person names to avoid conflicts)
        foreach (Match match in OrganizationRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
            {
                entities.Add(new ExtractedEntity(
                    match.Value,
                    "ORG",
                    match.Index,
                    match.Index + match.Length,
                    0.8f
                ));
            }
        }

        // Extract person names
        foreach (Match match in PersonNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
            {
                entities.Add(new ExtractedEntity(
                    match.Value,
                    "PERSON",
                    match.Index,
                    match.Index + match.Length,
                    0.7f
                ));
            }
        }

        // Extract locations
        foreach (Match match in LocationRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
            {
                entities.Add(new ExtractedEntity(
                    match.Value,
                    "GPE",
                    match.Index,
                    match.Index + match.Length,
                    0.6f
                ));
            }
        }

        // Extract years
        foreach (Match match in YearRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
            {
                var year = int.Parse(match.Value);
                if (year >= 1900 && year <= 2100) // Reasonable year range
                {
                    entities.Add(new ExtractedEntity(
                        match.Value,
                        "DATE",
                        match.Index,
                        match.Index + match.Length,
                        0.9f
                    ));
                }
            }
        }

        // Extract job titles
        foreach (Match match in TitleRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
            {
                entities.Add(new ExtractedEntity(
                    match.Value,
                    "TITLE",
                    match.Index,
                    match.Index + match.Length,
                    0.8f
                ));
            }
        }

        return Task.FromResult(entities.OrderBy(e => e.Start).ToList());
    }

    public Task<List<ExtractedRelationship>> ExtractRelationshipsAsync(string text, CancellationToken cancellationToken = default)
    {
        var relationships = new List<ExtractedRelationship>();

        // Pattern: "X works at Y" or "X is with Y"
        var worksAtPattern = new Regex(@"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:works? at|is with|employed by)\s+([A-Z][a-z]+(?: [A-Z][a-z]+)* (?:Inc|Corp|LLC|Ltd|Company)?)", RegexOptions.IgnoreCase);
        foreach (Match match in worksAtPattern.Matches(text))
        {
            relationships.Add(new ExtractedRelationship(
                match.Groups[1].Value,
                match.Groups[2].Value,
                "WORKS_AT",
                0.8f
            ));
        }

        // Pattern: "X founded Y" or "X created Y"
        var foundedPattern = new Regex(@"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:founded|created|started)\s+([A-Z][a-z]+(?: [A-Z][a-z]+)* (?:Inc|Corp|LLC|Ltd|Company)?)", RegexOptions.IgnoreCase);
        foreach (Match match in foundedPattern.Matches(text))
        {
            relationships.Add(new ExtractedRelationship(
                match.Groups[1].Value,
                match.Groups[2].Value,
                "FOUNDED",
                0.9f
            ));
        }

        // Pattern: "X lives in Y" or "X is from Y"
        var livesInPattern = new Regex(@"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:lives? in|is from|resides in)\s+([A-Z][a-z]+(?: [A-Z][a-z]+)*)", RegexOptions.IgnoreCase);
        foreach (Match match in livesInPattern.Matches(text))
        {
            relationships.Add(new ExtractedRelationship(
                match.Groups[1].Value,
                match.Groups[2].Value,
                "LIVES_IN",
                0.7f
            ));
        }

        // Pattern: "X met Y" or "X knows Y"
        var meetsPattern = new Regex(@"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:met|knows|knew)\s+([A-Z][a-z]+ [A-Z][a-z]+)", RegexOptions.IgnoreCase);
        foreach (Match match in meetsPattern.Matches(text))
        {
            relationships.Add(new ExtractedRelationship(
                match.Groups[1].Value,
                match.Groups[2].Value,
                "KNOWS",
                0.6f
            ));
        }

        return Task.FromResult(relationships);
    }

    public async Task<(List<ExtractedEntity> Entities, List<ExtractedRelationship> Relationships)> ExtractAllAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var entities = await ExtractEntitiesAsync(text, cancellationToken);
        var relationships = await ExtractRelationshipsAsync(text, cancellationToken);
        return (entities, relationships);
    }

    private static bool IsOverlapping(List<ExtractedEntity> entities, int start, int end)
    {
        return entities.Any(e =>
            (start >= e.Start && start < e.End) ||
            (end > e.Start && end <= e.End) ||
            (start <= e.Start && end >= e.End));
    }
}
