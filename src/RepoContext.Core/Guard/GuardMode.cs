namespace RepoContext.Core.Guard;

/// <summary>How the read-cost guard acts on an expensive read.</summary>
/// <remarks>
/// The guard is an <b>optimization</b>, never an access-control boundary. No
/// mode grants a read: the strongest thing any mode emits is a denial plus a
/// cheaper next call, so a client permission, an approval rule or a repository
/// exclusion can never be widened by installing it.
/// </remarks>
public enum GuardMode
{
    /// <summary>Inspect nothing, decide nothing. The state of an uninstalled guard.</summary>
    Off,

    /// <summary>
    /// Evaluate and count, but let every read through. The initial default of a
    /// newly installed guard: it measures coverage on a real workload before it
    /// is allowed to change one.
    /// </summary>
    Observe,

    /// <summary>
    /// Deny a supported expensive read once and name the cheaper call. Bounded:
    /// a repeated request for the same file is allowed through, so no agent can
    /// be trapped between a denial and the evidence it genuinely needs.
    /// </summary>
    Enforce,
}

/// <summary>Parsing for the mode names used on the command line and in hooks.</summary>
public static class GuardModes
{
    /// <summary>The mode a freshly installed guard runs in.</summary>
    public const string DefaultName = "observe";

    /// <summary>Parses a mode name case-insensitively; unknown names fail.</summary>
    public static bool TryParse(string? value, out GuardMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "off" or "disabled":
                mode = GuardMode.Off;
                return true;
            case null or "" or "observe":
                mode = GuardMode.Observe;
                return true;
            case "enforce":
                mode = GuardMode.Enforce;
                return true;
            default:
                mode = GuardMode.Observe;
                return false;
        }
    }

    /// <summary>The canonical lowercase name of a mode.</summary>
    public static string Name(GuardMode mode) => mode switch
    {
        GuardMode.Off => "off",
        GuardMode.Enforce => "enforce",
        _ => "observe",
    };
}
