using RepoContext.Core.Configuration;
using RepoContext.Core.Context;

namespace RepoContext.Core.Tests.Context;

/// <summary>
/// Tests for the <c>--detail auto</c> rule table (ADR 0018). The rules exist to
/// remove a corrective round trip, so what matters is that a task phrased as a
/// change gets source and a task phrased as a question gets breadth.
/// </summary>
public sealed class DetailPolicyTests
{
    [Theory]
    [InlineData("fix the login redirect")]
    [InlineData("implement token refresh")]
    [InlineData("refactor the session store")]
    [InlineData("debug the failing upload")]
    [InlineData("beheben des fehlers beim login")]
    public void ChangeTasks_GetSourceSpans(string task)
    {
        DetailChoice choice = Resolve(task);

        Assert.Equal(ContextDetail.Slices, choice.Detail);
        Assert.Equal(DetailChoiceReason.Action, choice.Reason);
        Assert.Equal("auto:action", choice.Label);
    }

    [Theory]
    [InlineData("where do we validate sessions")]
    [InlineData("which modules depend on the parser")]
    [InlineData("architecture of the indexing pipeline")]
    [InlineData("wo finden wir die konfiguration")]
    public void SurveyQuestions_GetOutlines(string task)
    {
        DetailChoice choice = Resolve(task);

        Assert.Equal(ContextDetail.Outline, choice.Detail);
        Assert.Equal(DetailChoiceReason.Survey, choice.Reason);
        Assert.Equal("auto:survey", choice.Label);
    }

    [Fact]
    public void AnUnclassifiedTask_FallsBackToSourceSpans()
    {
        DetailChoice choice = Resolve("token budget packing");

        Assert.Equal(ContextDetail.Slices, choice.Detail);
        Assert.Equal(DetailChoiceReason.Default, choice.Reason);
    }

    [Fact]
    public void AChangeTaskThatAlsoAsksWhere_StillGetsSourceSpans()
    {
        // The survey half is answerable from the slices; the change half is not
        // answerable from an outline, so action wins.
        DetailChoice choice = Resolve("find where we validate the token and fix it");

        Assert.Equal(ContextDetail.Slices, choice.Detail);
        Assert.Equal(DetailChoiceReason.Action, choice.Reason);
    }

    [Fact]
    public void AnEmptyQuery_IsStillResolvable()
    {
        Assert.Equal(ContextDetail.Slices, Resolve(string.Empty).Detail);
    }

    [Fact]
    public void ConfiguredSynonyms_CanReachTheRuleTable()
    {
        var config = RepoctxConfig.CreateDefault() with
        {
            Ranking = RepoctxConfig.CreateDefault().Ranking with
            {
                Synonyms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["reparieren"] = new[] { "fix" },
                },
            },
        };

        DetailChoice choice = DetailPolicy.Resolve(
            QueryAnalyzer.Analyze("reparieren des uploads", config).Terms);

        Assert.Equal(DetailChoiceReason.Action, choice.Reason);
    }

    private static DetailChoice Resolve(string task) =>
        DetailPolicy.Resolve(QueryAnalyzer.Analyze(task, RepoctxConfig.CreateDefault()).Terms);
}
