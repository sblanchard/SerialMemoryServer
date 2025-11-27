using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using System.Text.RegularExpressions;

namespace SerialMemory.ML;

/// <summary>
/// Software-aware entity extraction service.
/// Extends pattern-based extraction with software engineering entities.
/// </summary>
public partial class SoftwareEntityExtractionService : IEntityExtractionService
{
    // =========================================================================
    // GENERIC ENTITY PATTERNS (from PatternEntityExtractionService)
    // =========================================================================

    [GeneratedRegex(@"\b[A-Z][a-z]+ [A-Z][a-z]+\b", RegexOptions.Compiled)]
    private static partial Regex PersonNameRegex();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?: [A-Z][a-z]+)* (?:Inc|Corp|LLC|Ltd|Company|Corporation)\b", RegexOptions.Compiled)]
    private static partial Regex OrganizationRegex();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?: [A-Z][a-z]+)*,? [A-Z]{2}\b", RegexOptions.Compiled)]
    private static partial Regex LocationRegex();

    [GeneratedRegex(@"\b\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    // =========================================================================
    // SOFTWARE ENTITY PATTERNS
    // =========================================================================

    // GitHub/GitLab repo patterns (org/repo format)
    [GeneratedRegex(@"\b(?:github\.com|gitlab\.com|bitbucket\.org)[/:]([a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RepoUrlRegex();

    [GeneratedRegex(@"\b([a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+)(?:\s+(?:repo|repository))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RepoMentionRegex();

    // Service/API names (PascalCase or kebab-case with -service, -api suffix)
    [GeneratedRegex(@"\b([A-Z][a-zA-Z0-9]*(?:Service|API|Api|Worker|Handler|Controller))\b", RegexOptions.Compiled)]
    private static partial Regex ServiceNamePascalRegex();

    [GeneratedRegex(@"\b([a-z][a-z0-9]*(?:-[a-z0-9]+)*-(?:service|api|worker|handler))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ServiceNameKebabRegex();

    // Database names (PostgreSQL, MySQL, MongoDB, Redis, etc.)
    [GeneratedRegex(@"\b((?:postgresql?|mysql|mongodb|redis|elasticsearch|cassandra|dynamodb|cosmosdb|firestore|supabase|planetscale|neon|cockroach(?:db)?|timescale(?:db)?|influx(?:db)?)\s*(?:cluster|database|db|instance)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseTypeRegex();

    [GeneratedRegex(@"\b([a-z][a-z0-9_]*(?:_db|_database|_store|_cache))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseNameRegex();

    // Table names (snake_case, typically after "table" or in SQL context)
    [GeneratedRegex(@"\b(?:table|from|join|into)\s+([a-z][a-z0-9_]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TableNameRegex();

    // Technology/framework names
    [GeneratedRegex(@"\b(\.NET|ASP\.NET|React|Vue|Angular|Next\.js|Nuxt|Svelte|Node\.js|Express|FastAPI|Django|Flask|Spring(?:\s*Boot)?|Rails|Laravel|Phoenix|NestJS|Deno|Bun|Astro|Remix|Qwik|SolidJS|Blazor|MAUI|WPF|WinForms|Electron|Tauri|Flutter|React\s*Native|Expo|Capacitor|Ionic|Kotlin|Swift|Rust|Go(?:lang)?|Python|TypeScript|JavaScript|C#|F#|Java|Scala|Clojure|Elixir|Erlang|Haskell|OCaml|Ruby|PHP|Perl|Lua|Zig|Nim|Crystal|Julia|R|MATLAB|Dart|Objective-C)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TechnologyRegex();

    // Version patterns (v1.2.3, version 1.2.3, release 1.2.3)
    [GeneratedRegex(@"\b(?:v(?:ersion)?|release)\s*(\d+(?:\.\d+)*(?:-[a-zA-Z0-9.]+)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\bv(\d+(?:\.\d+)*(?:-[a-zA-Z0-9.]+)?)\b", RegexOptions.Compiled)]
    private static partial Regex SemverRegex();

    // Environment names
    [GeneratedRegex(@"\b((?:dev(?:elopment)?|staging|stg|uat|qa|test(?:ing)?|prod(?:uction)?|local|sandbox|preview|canary|beta|alpha)\s*(?:env(?:ironment)?|server|cluster)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentRegex();

    // API endpoint patterns
    [GeneratedRegex(@"\b((?:GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+/[a-zA-Z0-9/_\-{}:]+)\b", RegexOptions.Compiled)]
    private static partial Regex ApiEndpointRegex();

    [GeneratedRegex(@"\b(/(?:api|v\d+)/[a-zA-Z0-9/_\-{}:]+)\b", RegexOptions.Compiled)]
    private static partial Regex ApiPathRegex();

    // Message queue/event names
    [GeneratedRegex(@"\b([a-z][a-z0-9._-]*(?:\.(?:created|updated|deleted|processed|failed|completed|started|finished|event|message|command|query))+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EventNameRegex();

    [GeneratedRegex(@"\b([a-zA-Z][a-zA-Z0-9]*(?:Event|Message|Command|Query|Request|Response))\b", RegexOptions.Compiled)]
    private static partial Regex MessageTypeRegex();

    // Config file patterns
    [GeneratedRegex(@"\b([a-z][a-z0-9._-]*\.(?:json|yaml|yml|toml|env|config|conf|ini|xml|properties))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ConfigFileRegex();

    // Module/package names (npm, NuGet, PyPI patterns)
    [GeneratedRegex(@"\b(?:npm|yarn|pnpm)\s+(?:install|add|i)\s+([a-z@][a-z0-9@/_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex NpmPackageRegex();

    [GeneratedRegex(@"\b(?:dotnet\s+add\s+package|Install-Package|PackageReference)\s+([A-Za-z][A-Za-z0-9._]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex NugetPackageRegex();

    [GeneratedRegex(@"\b(?:pip\s+install|poetry\s+add)\s+([a-z][a-z0-9_-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PypiPackageRegex();

    // Bug/issue references
    [GeneratedRegex(@"\b(?:bug|issue|ticket|jira|fix(?:es)?|closes?|resolves?)\s*[#:]?\s*([A-Z]{2,10}-\d+|\d{4,})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BugReferenceRegex();

    // Test names
    [GeneratedRegex(@"\b([A-Z][a-zA-Z0-9]*(?:Test|Tests|Spec|Specs))\b", RegexOptions.Compiled)]
    private static partial Regex TestClassRegex();

    [GeneratedRegex(@"\b(?:test|it|describe|should)\s*[([\x22']([^)\x22']+)[)\x22']\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TestNameRegex();

    // Deployment/infrastructure
    [GeneratedRegex(@"\b((?:kubernetes|k8s|docker|helm|terraform|pulumi|cloudformation|aws|azure|gcp|heroku|vercel|netlify|railway|render|fly\.io)\s*(?:cluster|deployment|stack|infra(?:structure)?)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex InfrastructureRegex();

    // Incident references
    [GeneratedRegex(@"\b(?:incident|outage|pager|alert|sev-?\d|p\d)\s*[#:]?\s*([A-Z]{0,5}\d{4,}|[A-Z]{2,10}-\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex IncidentReferenceRegex();

    // Team names
    [GeneratedRegex(@"\b([A-Z][a-zA-Z0-9]*\s*(?:Team|Squad|Guild|Tribe|Chapter))\b", RegexOptions.Compiled)]
    private static partial Regex TeamNameRegex();

    // =========================================================================
    // SOFTWARE RELATIONSHIP PATTERNS
    // =========================================================================

    // X depends on Y / X uses Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:depends?\s+on|uses|requires|imports?|needs?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DependsOnRegex();

    // X calls Y / X invokes Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:calls?|invokes?|requests?|queries|fetches\s+from)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CallsRegex();

    // X deployed to Y / X runs on Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:deployed\s+(?:to|on)|runs?\s+on|hosted\s+(?:on|by))\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DeployedToRegex();

    // X owns Y / X maintains Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_\s.-]+)\s+(?:owns?|maintains?|manages?|is\s+responsible\s+for)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex OwnsRegex();

    // X implements Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:implements?|exposes?|provides?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ImplementsRegex();

    // X emits/publishes Y (events)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:emits?|publishes?|produces?|sends?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EmitsRegex();

    // X consumes/subscribes Y (events)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:consumes?|subscribes?\s+to|listens?\s+(?:to|for)|handles?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ConsumesRegex();

    // X fixes/caused Y (bugs/incidents)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:fixes?|fixed|resolves?|resolved)\s+([A-Za-z0-9_#.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FixesRegex();

    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:caused?|triggered?|introduced?|broke)\s+([A-Za-z0-9_#.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CausedRegex();

    // X tests Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:tests?|validates?|verifies?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TestsRegex();

    // X works on Y (person -> project/service)
    [GeneratedRegex(@"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:works?\s+on|developing|built|created|maintains?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex WorksOnRegex();

    public Task<List<ExtractedEntity>> ExtractEntitiesAsync(string text, CancellationToken cancellationToken = default)
    {
        var entities = new List<ExtractedEntity>();

        // =====================================================================
        // SOFTWARE ENTITIES (higher priority)
        // =====================================================================

        // Repositories
        foreach (Match match in RepoUrlRegex().Matches(text))
        {
            entities.Add(new ExtractedEntity(match.Groups[1].Value, "REPO", match.Index, match.Index + match.Length, 0.95f));
        }
        foreach (Match match in RepoMentionRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "REPO", match.Index, match.Index + match.Length, 0.8f));
        }

        // Services/APIs (PascalCase)
        foreach (Match match in ServiceNamePascalRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "SERVICE", match.Index, match.Index + match.Length, 0.85f));
        }

        // Services/APIs (kebab-case)
        foreach (Match match in ServiceNameKebabRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "SERVICE", match.Index, match.Index + match.Length, 0.85f));
        }

        // API endpoints
        foreach (Match match in ApiEndpointRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "API", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in ApiPathRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "API", match.Index, match.Index + match.Length, 0.85f));
        }

        // Databases
        foreach (Match match in DatabaseTypeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "DATABASE", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in DatabaseNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "DATABASE", match.Index, match.Index + match.Length, 0.75f));
        }

        // Tables
        foreach (Match match in TableNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TABLE", match.Index, match.Index + match.Length, 0.85f));
        }

        // Technologies/frameworks
        foreach (Match match in TechnologyRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TECH", match.Index, match.Index + match.Length, 0.95f));
        }

        // Versions
        foreach (Match match in VersionRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "VERSION", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in SemverRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "VERSION", match.Index, match.Index + match.Length, 0.85f));
        }

        // Environments
        foreach (Match match in EnvironmentRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "ENV", match.Index, match.Index + match.Length, 0.9f));
        }

        // Events/messages
        foreach (Match match in EventNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MESSAGE", match.Index, match.Index + match.Length, 0.85f));
        }
        foreach (Match match in MessageTypeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MESSAGE", match.Index, match.Index + match.Length, 0.8f));
        }

        // Config files
        foreach (Match match in ConfigFileRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "CONFIG", match.Index, match.Index + match.Length, 0.9f));
        }

        // Modules/packages
        foreach (Match match in NpmPackageRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MODULE", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in NugetPackageRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MODULE", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in PypiPackageRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MODULE", match.Index, match.Index + match.Length, 0.9f));
        }

        // Bugs/issues
        foreach (Match match in BugReferenceRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "BUG", match.Index, match.Index + match.Length, 0.9f));
        }

        // Tests
        foreach (Match match in TestClassRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TEST", match.Index, match.Index + match.Length, 0.85f));
        }
        foreach (Match match in TestNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TEST", match.Index, match.Index + match.Length, 0.8f));
        }

        // Infrastructure/deployment
        foreach (Match match in InfrastructureRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "DEPLOYMENT", match.Index, match.Index + match.Length, 0.85f));
        }

        // Incidents
        foreach (Match match in IncidentReferenceRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "INCIDENT", match.Index, match.Index + match.Length, 0.9f));
        }

        // Teams
        foreach (Match match in TeamNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TEAM", match.Index, match.Index + match.Length, 0.85f));
        }

        // =====================================================================
        // GENERIC ENTITIES (lower priority, avoid overlap with software entities)
        // =====================================================================

        // Emails
        foreach (Match match in EmailRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value, "PERSON", match.Index, match.Index + match.Length, 0.7f));
        }

        // Organizations
        foreach (Match match in OrganizationRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value, "ORG", match.Index, match.Index + match.Length, 0.8f));
        }

        // Person names (be more conservative to avoid matching service names)
        foreach (Match match in PersonNameRegex().Matches(text))
        {
            if (IsOverlapping(entities, match.Index, match.Index + match.Length)) continue;
            // Skip if it looks like a service name
            var value = match.Value;
            if (!value.EndsWith("Service") && !value.EndsWith("Api") && !value.EndsWith("Handler"))
            {
                entities.Add(new ExtractedEntity(value, "PERSON", match.Index, match.Index + match.Length, 0.6f));
            }
        }

        // Locations
        foreach (Match match in LocationRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value, "GPE", match.Index, match.Index + match.Length, 0.6f));
        }

        // Years/dates
        foreach (Match match in YearRegex().Matches(text))
        {
            if (IsOverlapping(entities, match.Index, match.Index + match.Length)) continue;
            var year = int.Parse(match.Value);
            if (year is >= 1900 and <= 2100)
                entities.Add(new ExtractedEntity(match.Value, "DATE", match.Index, match.Index + match.Length, 0.9f));
        }

        return Task.FromResult(entities.OrderBy(e => e.Start).ToList());
    }

    public Task<List<ExtractedRelationship>> ExtractRelationshipsAsync(string text, CancellationToken cancellationToken = default)
    {
        var relationships = new List<ExtractedRelationship>();

        // Software relationships
        foreach (Match match in DependsOnRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "DEPENDS_ON", 0.85f));

        foreach (Match match in CallsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "CALLS", 0.85f));

        foreach (Match match in DeployedToRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "DEPLOYED_TO", 0.85f));

        foreach (Match match in OwnsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value.Trim(), match.Groups[2].Value, "OWNS", 0.8f));

        foreach (Match match in ImplementsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "IMPLEMENTS", 0.85f));

        foreach (Match match in EmitsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "EMITS", 0.85f));

        foreach (Match match in ConsumesRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "CONSUMES", 0.85f));

        foreach (Match match in FixesRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "FIXES", 0.9f));

        foreach (Match match in CausedRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "CAUSED", 0.85f));

        foreach (Match match in TestsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "TESTS", 0.85f));

        foreach (Match match in WorksOnRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "WORKS_ON", 0.8f));

        // Normalize relationship types - replace invalid types with RELATED_TO
        var normalizedRelationships = relationships.Select(rel =>
            SoftwareGraphTypes.IsValidRelationshipType(rel.RelationType)
                ? rel
                : new ExtractedRelationship(rel.SourceEntity, rel.TargetEntity, "RELATED_TO", rel.Confidence * 0.8f)
        ).ToList();

        return Task.FromResult(normalizedRelationships);
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
