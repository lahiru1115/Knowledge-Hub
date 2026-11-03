using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>
/// The contract is written first and the API follows it (Doc 02 §8.1). This keeps the two from drifting apart: every
/// operation in contracts/openapi.yaml must be served, and every route the API serves under /api/v1 must be in the
/// contract. Operations that are in the contract but not built yet are listed here, so building one means removing it.
/// </summary>
[Collection(ApiTestGroup.Name)]
public partial class ContractDriftTests(ApiFactory factory)
{
    /// <summary>Could requirements that are specified but not built (FR-SRCH-05, FR-AUTH-10).</summary>
    private static readonly HashSet<string> NotBuiltYet =
    [
        "GET /views", "POST /views", "PATCH /views/{}", "DELETE /views/{}",
        "GET /auth/oauth/{}", "GET /auth/oauth/{}/callback",
    ];

    [GeneratedRegex(@"^  (/\S*):\s*$")]
    private static partial Regex PathLine();

    [GeneratedRegex(@"^    (get|post|put|patch|delete):\s*$")]
    private static partial Regex MethodLine();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Parameter();

    private static string Shape(string path) => Parameter().Replace(path, "{}");

    private static HashSet<string> ContractOperations()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "contracts", "openapi.yaml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var ops = new HashSet<string>();
        var inPaths = false;
        string? path = null;
        foreach (var line in File.ReadLines(Path.Combine(dir.FullName, "contracts", "openapi.yaml")))
        {
            if (line.StartsWith("paths:", StringComparison.Ordinal)) { inPaths = true; continue; }
            if (inPaths && line.Length > 0 && !char.IsWhiteSpace(line[0])) break;   // the next top-level key
            if (!inPaths) continue;
            if (PathLine().Match(line) is { Success: true } p) path = Shape(p.Groups[1].Value);
            else if (path is not null && MethodLine().Match(line) is { Success: true } m)
            {
                if (!path.StartsWith("/health/", StringComparison.Ordinal))   // served at /health, outside /api/v1 (IR-17)
                    ops.Add($"{m.Groups[1].Value.ToUpperInvariant()} {path}");
            }
        }

        return ops;
    }

    private HashSet<string> ServedOperations()
    {
        var served = new HashSet<string>();
        foreach (var e in factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var text = "/" + e.RoutePattern.RawText?.TrimStart('/');
            if (!text.StartsWith("/api/v1", StringComparison.Ordinal)) continue;   // health checks sit outside it
            var path = Shape(Regex.Replace(text["/api/v1".Length..], @"\{(\w+)[^}]*\}", "{$1}"));
            path = path.TrimEnd('/');
            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                served.Add($"{method} {path}");
        }

        return served;
    }

    [Fact]
    public void Every_operation_in_the_contract_is_served_unless_it_is_listed_as_not_built_yet()
    {
        var missing = ContractOperations().Except(ServedOperations()).Except(NotBuiltYet).Order().ToList();
        Assert.True(missing.Count == 0, "In the contract but not served: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_route_the_api_serves_is_in_the_contract()
    {
        var undocumented = ServedOperations().Except(ContractOperations()).Order().ToList();
        Assert.True(undocumented.Count == 0, "Served but not in the contract: " + string.Join(", ", undocumented));
    }

    [Fact]
    public void The_not_built_yet_list_only_holds_operations_that_are_still_missing()
    {
        var built = NotBuiltYet.Intersect(ServedOperations()).Order().ToList();
        Assert.True(built.Count == 0, "Built now, so remove from the list: " + string.Join(", ", built));
        Assert.Empty(NotBuiltYet.Except(ContractOperations()));
    }

    [Fact]
    public void The_contract_parser_finds_the_operations()
    {
        var ops = ContractOperations();
        Assert.True(ops.Count >= 50, $"Only {ops.Count} operations found");
        Assert.Contains("GET /resources/{}", ops);
        Assert.Contains("POST /resources/bulk", ops);
    }
}
