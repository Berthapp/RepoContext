using RepoContext.Core.Context;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// The frozen Release 1 evaluation corpus (Q0). Small and purpose-built rather
/// than scraped from this repository, so labels stay stable and reviewable.
/// </summary>
/// <remarks>
/// These labels are the contract the relevance gates are measured against. They
/// are a Release 1 candidate corpus frozen together with the first implementation;
/// they cannot provide a pre-change comparison retroactively. Once checked in, a
/// product change may never edit them to pass a gate. New tasks are added when a
/// real failure is found, not to raise a score.
/// </remarks>
public static class EvalCorpus
{
    public static IReadOnlyList<EvalTask> Tasks { get; } =
    [
        new EvalTask
        {
            Id = "locate-cs-packer",
            Query = "where is the budget packing logic",
            Class = TaskClass.Locate,
            Language = LanguageStratum.CSharp,
            MustFindFiles = ["src/Packing/Packer.cs"],
            ForbiddenPaths = [".env"],
            Detail = ContextDetail.Paths,
            ResponseBudgetTokens = 3000,
            FullReadExpected = true,
        },
        new EvalTask
        {
            Id = "fix-cs-budget",
            Query = "change budget packing",
            Class = TaskClass.Fix,
            Language = LanguageStratum.CSharp,
            MustFindFiles = ["src/Packing/Packer.cs"],
            MustCoverSpans = [new RequiredSpan("src/Packing/Packer.cs", 115, 139)],
            ForbiddenPaths = [".env"],
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = 3000,
            FullReadExpected = false,
        },
        new EvalTask
        {
            Id = "explain-cs-envelope",
            Query = "envelope tokens",
            Class = TaskClass.Explain,
            Language = LanguageStratum.CSharp,
            MustFindFiles = ["src/Packing/Packer.cs"],
            MustFindSymbols = ["EnvelopeTokens"],
            ForbiddenPaths = [".env"],
            Detail = ContextDetail.Outline,
            ResponseBudgetTokens = 3000,
            FullReadExpected = false,
        },
        new EvalTask
        {
            Id = "locate-ts-login",
            Query = "login user credentials",
            Class = TaskClass.Locate,
            Language = LanguageStratum.TypeScript,
            MustFindFiles = ["src/auth/login.ts"],
            ForbiddenPaths = [".env"],
            Detail = ContextDetail.Paths,
            ResponseBudgetTokens = 3000,
            FullReadExpected = true,
        },
        new EvalTask
        {
            Id = "fix-ts-session-validity",
            Query = "change session validity check",
            Class = TaskClass.Fix,
            Language = LanguageStratum.TypeScript,
            MustFindFiles = ["src/auth/session.ts"],
            MustCoverSpans = [new RequiredSpan("src/auth/session.ts", 16, 18)],
            ForbiddenPaths = [".env"],
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = 3000,
            FullReadExpected = false,
        },
        new EvalTask
        {
            Id = "explain-ts-session",
            Query = "session lifetime",
            Class = TaskClass.Explain,
            Language = LanguageStratum.TypeScript,
            MustFindFiles = ["src/auth/session.ts"],
            MustFindSymbols = ["createSession"],
            ForbiddenPaths = [".env"],
            Detail = ContextDetail.Outline,
            ResponseBudgetTokens = 3000,
            FullReadExpected = false,
        },
        new EvalTask
        {
            Id = "impact-ts-session",
            Query = "session",
            Class = TaskClass.Impact,
            Language = LanguageStratum.TypeScript,
            // Both files must surface for this impact proxy. Relation kind and
            // direction are not yet explicit labels in the candidate corpus.
            MustFindFiles = ["src/auth/session.ts", "src/auth/login.ts"],
            ForbiddenPaths = [".env"],
            Detail = ContextDetail.Paths,
            ResponseBudgetTokens = 3000,
            FullReadExpected = true,
        },
    ];
}
