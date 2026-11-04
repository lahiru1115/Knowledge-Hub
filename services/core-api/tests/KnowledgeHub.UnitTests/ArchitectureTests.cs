using NetArchTest.Rules;
using Xunit;

namespace KnowledgeHub.UnitTests;

/// <summary>NFR-MAIN-05: dependencies point inward (Doc 02 §3.1).</summary>
public class ArchitectureTests
{
    private const string Domain = "KnowledgeHub.Domain";
    private const string Application = "KnowledgeHub.Application";
    private const string Infrastructure = "KnowledgeHub.Infrastructure";
    private const string Api = "KnowledgeHub.Api";

    [Fact]
    public void Domain_depends_on_nothing_else_in_the_solution() =>
        AssertNoDependency(typeof(Domain.User), Application, Infrastructure, Api, "Microsoft.EntityFrameworkCore");

    [Fact]
    public void Application_does_not_depend_on_Infrastructure_or_Api() =>
        AssertNoDependency(typeof(Application.Common.IClock), Infrastructure, Api);

    [Fact]
    public void Infrastructure_does_not_depend_on_Api() =>
        AssertNoDependency(typeof(Infrastructure.DependencyInjection), Api);

    private static void AssertNoDependency(Type anchor, params string[] forbidden)
    {
        var result = Types.InAssembly(anchor.Assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
