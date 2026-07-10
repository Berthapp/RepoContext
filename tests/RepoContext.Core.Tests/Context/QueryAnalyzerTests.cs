using RepoContext.Core.Configuration;
using RepoContext.Core.Context;

namespace RepoContext.Core.Tests.Context;

public class QueryAnalyzerTests
{
    [Fact]
    public void Analyze_RemovesEnglishStopWords()
    {
        AnalyzedQuery result = QueryAnalyzer.Analyze("change the login logic", RepoctxConfig.CreateDefault());
        Assert.Equal(["change", "login", "logic"], result.Terms);
    }

    [Fact]
    public void Analyze_RemovesGermanStopWords()
    {
        AnalyzedQuery result = QueryAnalyzer.Analyze("Ich möchte die Login-Logik ändern", RepoctxConfig.CreateDefault());
        Assert.Contains("login", result.Terms);
        Assert.Contains("logik", result.Terms);
        Assert.Contains("ändern", result.Terms);
        Assert.DoesNotContain("ich", result.Terms);
        Assert.DoesNotContain("die", result.Terms);
    }

    [Fact]
    public void Analyze_ExpandsConfiguredSynonyms()
    {
        RepoctxConfig config = RepoctxConfig.CreateDefault() with
        {
            Ranking = new RankingOptions
            {
                Synonyms = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["zahlung"] = ["payment", "billing"],
                },
            },
        };

        AnalyzedQuery result = QueryAnalyzer.Analyze("zahlung ändern", config);
        Assert.Contains("payment", result.Terms);
        Assert.Contains("billing", result.Terms);
    }

    [Fact]
    public void Analyze_EmptyAfterStopWords_HasNullMatch()
    {
        AnalyzedQuery result = QueryAnalyzer.Analyze("the a to of", RepoctxConfig.CreateDefault());
        Assert.Null(result.FtsMatch);
    }
}
