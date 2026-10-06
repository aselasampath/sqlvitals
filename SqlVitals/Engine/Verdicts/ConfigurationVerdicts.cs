using System.Globalization;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Security;

namespace SqlVitals.Engine.Verdicts;

/// <summary>Config Checks' verdict (#150): how many checks fail, the highest risk first.</summary>
public static class ConfigChecksVerdict
{
    /// <summary>Selects the check the verdict names.</summary>
    public const string ShowCheck = "show-check";

    /// <summary>Opens the script with every warning's fix.</summary>
    public const string Script = "script";

    /// <summary>
    /// The checks from the most harmful when wrong: memory that starves Windows and corruption
    /// that goes unnoticed, then shrinking and parallelism, TempDB contention, plan changes.
    /// </summary>
    public static readonly IReadOnlyList<ConfigCheckKind> RiskOrder =
    [
        ConfigCheckKind.MaxServerMemory,
        ConfigCheckKind.PageVerify,
        ConfigCheckKind.AutoShrink,
        ConfigCheckKind.MaxDop,
        ConfigCheckKind.TempDbFileCount,
        ConfigCheckKind.CostThreshold,
        ConfigCheckKind.CompatibilityLevel,
        ConfigCheckKind.TempDbFileSizes,
    ];

    private static int Risk(ConfigCheckKind kind)
    {
        var index = RiskOrder.ToList().IndexOf(kind);
        return index < 0 ? RiskOrder.Count : index;
    }

    /// <summary>The warning the verdict names: the highest risk.</summary>
    public static ConfigCheck? Riskiest(ConfigCheckList list) =>
        list.Checks.Where(c => c.IsWarning).OrderBy(c => Risk(c.Kind)).FirstOrDefault();

    public static PageVerdict From(ConfigCheckList list)
    {
        var culture = CultureInfo.CurrentCulture;
        var problem = string.IsNullOrWhiteSpace(list.Problem) ? "" : " " + list.Problem.Trim();
        var notChecked = list.NotCheckedCount == 0 ? ""
            : string.Format(culture, " {0} couldn't be checked.", Sizes.Plural(list.NotCheckedCount, "setting", "settings"));

        if (list.Checks.Count == 0)
            return new PageVerdict(VerdictLevel.Unknown, "Nothing could be checked", (problem + notChecked).Trim());

        if (Riskiest(list) is not { } top)
            return new PageVerdict(list.PassCount == 0 ? VerdictLevel.Unknown : VerdictLevel.Ok,
                list.PassCount == 0 ? "Nothing could be checked" : "Every setting checked follows best practice",
                (string.Format(culture, "{0} passed.", Sizes.Plural(list.PassCount, "check", "checks")) + notChecked + problem).Trim());

        var headline = string.Format(culture, "{0} of {1} {2}; the riskiest: {3} ({4})",
                                     list.WarnCount, Sizes.Plural(list.Checks.Count, "check", "checks"), list.WarnCount == 1 ? "fails" : "fail",
                                     top.Name, top.Scope);
        var why = string.Format(culture, "{0}{1}. {2}", top.Current.Length > 0 ? top.Current : "Not as recommended",
                                top.Recommended.Length > 0 ? $", recommended {top.Recommended}" : "", top.Explanation.Trim())
                + notChecked + problem;
        List<VerdictAction> actions = [new(ShowCheck, "Show it")];
        if (list.Checks.Any(c => c.IsWarning && c.FixScript is not null))
            actions.Add(new(Script, "Script the warnings"));
        return new PageVerdict(VerdictLevel.Warning, headline, why, actions);
    }
}

/// <summary>Security Audit's verdict (#150): the risks found, the highest first, or what is left to review.</summary>
public static class SecurityAuditVerdict
{
    /// <summary>Selects the finding the verdict names.</summary>
    public const string ShowFinding = "show-finding";

    /// <summary>Opens the script with every risk's fix.</summary>
    public const string Script = "script";

    /// <summary>The finding the verdict names: a risk, else something to review; sa first, then sysadmin, TRUSTWORTHY, orphans.</summary>
    public static SecurityFinding? Subject(SecurityFindingList list) => list.Findings
        .Where(f => f.Status is SecurityFindingStatus.Risk or SecurityFindingStatus.Review)
        .OrderBy(f => f.Status).ThenBy(f => f.Kind).FirstOrDefault();

    public static PageVerdict From(SecurityFindingList list)
    {
        var culture = CultureInfo.CurrentCulture;
        if (!list.Available)
            return new PageVerdict(VerdictLevel.Unknown, "Couldn't audit the logins and databases", list.Problem);

        var problem = string.IsNullOrWhiteSpace(list.Problem) ? "" : " " + list.Problem.Trim();
        var notChecked = list.NotCheckedCount == 0 ? ""
            : string.Format(culture, " {0} couldn't be checked.", Sizes.Plural(list.NotCheckedCount, "item", "items"));

        if (Subject(list) is not { } top)
            return new PageVerdict(list.PassCount == 0 ? VerdictLevel.Unknown : VerdictLevel.Ok,
                list.PassCount == 0 ? "Nothing could be audited" : "Nothing audited widens the attack surface",
                ("sa, sysadmin membership, TRUSTWORTHY and orphaned users all pass." + notChecked + problem).Trim());

        // "sysadmin member: CORP\jane", but "sa login", not "sa login: sa".
        var subject = top.Subject.Length > 0 && top.Subject != top.Scope && !top.Name.Contains(top.Subject, StringComparison.OrdinalIgnoreCase)
            ? $"{top.Name}: {top.Subject}" : top.Name;
        var headline = list.RiskCount > 0
            ? string.Format(culture, "{0} found; the highest: {1} ({2})", Sizes.Plural(list.RiskCount, "risk", "risks"), subject, top.Finding)
            : string.Format(culture, "No risk found; {0} to review, first {1}", list.ReviewCount, subject);
        var why = top.Explanation.Trim()
                + (list.RiskCount > 0 && list.ReviewCount > 0 ? string.Format(culture, " Also {0:N0} to review.", list.ReviewCount) : "")
                + notChecked + problem;
        List<VerdictAction> actions = [new(ShowFinding, "Show it")];
        if (list.Findings.Any(f => f.IsRisk && f.FixScript is not null))
            actions.Add(new(Script, "Script the risks"));
        return new PageVerdict(list.RiskCount > 0 ? VerdictLevel.Warning : VerdictLevel.Info, headline, why, actions);
    }
}
