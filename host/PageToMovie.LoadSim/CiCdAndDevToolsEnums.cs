using System.Text.Json.Serialization;

namespace PageToMovie.LoadSim;

/// <summary>
/// Execution stage categories in CI/CD build and delivery pipelines.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CiCdPipelineStage
{
    Lint,
    Build,
    UnitTest,
    IntegrationTest,
    SecurityScan,
    Package,
    DeployStaging,
    DeployProduction,
    Unknown
}

/// <summary>
/// Git branching strategies and branch classifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitBranchType
{
    Main,
    Develop,
    Feature,
    Bugfix,
    Release,
    Hotfix,
    Experimental,
    Unknown
}

/// <summary>
/// Code coverage granularity metrics measured during test execution.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeCoverageMetric
{
    LineCoverage,
    BranchCoverage,
    MethodCoverage,
    StatementCoverage,
    ConditionCoverage,
    Unknown
}

/// <summary>
/// Severity levels for static code analysis and linting findings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StaticAnalysisSeverity
{
    Info,
    Warning,
    Error,
    Critical,
    Blocker,
    Unknown
}

/// <summary>
/// Artifact repository registry target types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PackageRegistryType
{
    NuGet,
    Npm,
    DockerHub,
    GitHubPackages,
    PyPI,
    Maven,
    Cargo,
    Unknown
}

/// <summary>
/// Output artifact formats produced by build pipelines.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildArtifactType
{
    ExecutableBinary,
    DockerImage,
    WasmBundle,
    NugetPackage,
    NpmPackage,
    ZipArchive,
    TestReport,
    Unknown
}

/// <summary>
/// Software deployment distribution release channels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReleaseChannel
{
    Alpha,
    Beta,
    ReleaseCandidate,
    Stable,
    Nightly,
    Lts,
    Unknown
}

/// <summary>
/// Semantic release notes and changelog entry categories.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangelogCategory
{
    Feature,
    Fix,
    Performance,
    Documentation,
    Refactor,
    Deprecation,
    Security,
    BreakingChange,
    Unknown
}

/// <summary>
/// Semantic versioning (SemVer) increment types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SemVerBumpType
{
    Major,
    Minor,
    Patch,
    PreRelease,
    BuildMetadata,
    None,
    Unknown
}

/// <summary>
/// Code style and linter rule enforcement presets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LinterRuleSet
{
    Recommended,
    Strict,
    SecurityFocused,
    StyleOnly,
    Custom,
    Disabled,
    Unknown
}

/// <summary>
/// Standard formats for test execution report outputs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestReportFormat
{
    Trx,
    JunitXml,
    NunitXml,
    HtmlSummary,
    CoberturaXml,
    ConsoleOutput,
    Unknown
}

/// <summary>
/// Conventional Commit specification commit message types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitCommitType
{
    Feat,
    Fix,
    Docs,
    Style,
    Refactor,
    Perf,
    Test,
    Chore,
    Ci,
    Build,
    Revert,
    Unknown
}

/// <summary>
/// Automated dependency update bump strategies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyUpdateStrategy
{
    LockfileOnly,
    MinorAndPatch,
    PatchOnly,
    LatestCompatible,
    ManualApproval,
    Unknown
}

/// <summary>
/// Operating system platforms hosting CI build runners.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildRunnerOS
{
    UbuntuLatest,
    WindowsLatest,
    MacOsLatest,
    SelfHostedLinux,
    SelfHostedWindows,
    Unknown
}

/// <summary>
/// Quality gate evaluation result states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeQualityGateResult
{
    Passed,
    Failed,
    Warning,
    Skipped,
    Pending,
    Unknown
}

/// <summary>
/// AI agent execution autonomy modes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentWorkMode
{
    Autonomous,
    SemiAutonomous,
    Supervised,
    Interactive,
    DryRun,
    Unknown
}

/// <summary>
/// Peer code review status states in pull requests.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeReviewStatus
{
    PendingReview,
    Approved,
    ChangesRequested,
    Draft,
    ClosedUnmerged,
    Unknown
}

/// <summary>
/// Open-source and commercial software license classifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RepositoryLicense
{
    MIT,
    Apache20,
    GPLv3,
    BSD3Clause,
    AGPLv3,
    Proprietary,
    Unlicense,
    Unknown
}

/// <summary>
/// Defect and task issue priority levels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssuePriorityLevel
{
    Low,
    Medium,
    High,
    Urgent,
    Critical,
    Unknown
}

/// <summary>
/// Project roadmap milestone progress statuses.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectMilestoneStatus
{
    Planned,
    InProgress,
    FeatureComplete,
    InTesting,
    Closed,
    Overdue,
    Unknown
}

/// <summary>
/// Extension methods for CI/CD pipeline and developer tools enums.
/// </summary>
public static class CiCdAndDevToolsEnumExtensions
{
    public static string ToApiString(this CiCdPipelineStage value) => value switch
    {
        CiCdPipelineStage.Lint => "lint",
        CiCdPipelineStage.Build => "build",
        CiCdPipelineStage.UnitTest => "unit_test",
        CiCdPipelineStage.IntegrationTest => "integration_test",
        CiCdPipelineStage.SecurityScan => "security_scan",
        CiCdPipelineStage.Package => "package",
        CiCdPipelineStage.DeployStaging => "deploy_staging",
        CiCdPipelineStage.DeployProduction => "deploy_production",
        _ => "unknown"
    };

    public static CiCdPipelineStage ParseCiCdPipelineStage(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "lint" => CiCdPipelineStage.Lint,
            "build" => CiCdPipelineStage.Build,
            "unit_test" or "unittest" => CiCdPipelineStage.UnitTest,
            "integration_test" or "integrationtest" => CiCdPipelineStage.IntegrationTest,
            "security_scan" or "securityscan" => CiCdPipelineStage.SecurityScan,
            "package" => CiCdPipelineStage.Package,
            "deploy_staging" or "deploystaging" => CiCdPipelineStage.DeployStaging,
            "deploy_production" or "deployproduction" => CiCdPipelineStage.DeployProduction,
            _ => CiCdPipelineStage.Build
        };

    public static string ToApiString(this GitBranchType value) => value switch
    {
        GitBranchType.Main => "main",
        GitBranchType.Develop => "develop",
        GitBranchType.Feature => "feature",
        GitBranchType.Bugfix => "bugfix",
        GitBranchType.Release => "release",
        GitBranchType.Hotfix => "hotfix",
        GitBranchType.Experimental => "experimental",
        _ => "unknown"
    };

    public static GitBranchType ParseGitBranchType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "main" or "master" => GitBranchType.Main,
            "develop" or "dev" => GitBranchType.Develop,
            "feature" or "feat" => GitBranchType.Feature,
            "bugfix" or "fix" => GitBranchType.Bugfix,
            "release" => GitBranchType.Release,
            "hotfix" => GitBranchType.Hotfix,
            "experimental" or "exp" => GitBranchType.Experimental,
            _ => GitBranchType.Feature
        };

    public static string ToApiString(this CodeCoverageMetric value) => value switch
    {
        CodeCoverageMetric.LineCoverage => "line_coverage",
        CodeCoverageMetric.BranchCoverage => "branch_coverage",
        CodeCoverageMetric.MethodCoverage => "method_coverage",
        CodeCoverageMetric.StatementCoverage => "statement_coverage",
        CodeCoverageMetric.ConditionCoverage => "condition_coverage",
        _ => "unknown"
    };

    public static CodeCoverageMetric ParseCodeCoverageMetric(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "line_coverage" or "linecoverage" or "line" => CodeCoverageMetric.LineCoverage,
            "branch_coverage" or "branchcoverage" or "branch" => CodeCoverageMetric.BranchCoverage,
            "method_coverage" or "methodcoverage" or "method" => CodeCoverageMetric.MethodCoverage,
            "statement_coverage" or "statementcoverage" or "statement" => CodeCoverageMetric.StatementCoverage,
            "condition_coverage" or "conditioncoverage" or "condition" => CodeCoverageMetric.ConditionCoverage,
            _ => CodeCoverageMetric.LineCoverage
        };

    public static string ToApiString(this StaticAnalysisSeverity value) => value switch
    {
        StaticAnalysisSeverity.Info => "info",
        StaticAnalysisSeverity.Warning => "warning",
        StaticAnalysisSeverity.Error => "error",
        StaticAnalysisSeverity.Critical => "critical",
        StaticAnalysisSeverity.Blocker => "blocker",
        _ => "unknown"
    };

    public static StaticAnalysisSeverity ParseStaticAnalysisSeverity(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "info" or "information" => StaticAnalysisSeverity.Info,
            "warning" or "warn" => StaticAnalysisSeverity.Warning,
            "error" or "err" => StaticAnalysisSeverity.Error,
            "critical" or "crit" => StaticAnalysisSeverity.Critical,
            "blocker" => StaticAnalysisSeverity.Blocker,
            _ => StaticAnalysisSeverity.Warning
        };

    public static string ToApiString(this PackageRegistryType value) => value switch
    {
        PackageRegistryType.NuGet => "nuget",
        PackageRegistryType.Npm => "npm",
        PackageRegistryType.DockerHub => "docker_hub",
        PackageRegistryType.GitHubPackages => "github_packages",
        PackageRegistryType.PyPI => "pypi",
        PackageRegistryType.Maven => "maven",
        PackageRegistryType.Cargo => "cargo",
        _ => "unknown"
    };

    public static PackageRegistryType ParsePackageRegistryType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "nuget" => PackageRegistryType.NuGet,
            "npm" => PackageRegistryType.Npm,
            "docker_hub" or "dockerhub" or "docker" => PackageRegistryType.DockerHub,
            "github_packages" or "githubpackages" or "ghcr" => PackageRegistryType.GitHubPackages,
            "pypi" or "python" => PackageRegistryType.PyPI,
            "maven" or "java" => PackageRegistryType.Maven,
            "cargo" or "crates" => PackageRegistryType.Cargo,
            _ => PackageRegistryType.NuGet
        };

    public static string ToApiString(this BuildArtifactType value) => value switch
    {
        BuildArtifactType.ExecutableBinary => "executable_binary",
        BuildArtifactType.DockerImage => "docker_image",
        BuildArtifactType.WasmBundle => "wasm_bundle",
        BuildArtifactType.NugetPackage => "nupkg",
        BuildArtifactType.NpmPackage => "npm_package",
        BuildArtifactType.ZipArchive => "zip_archive",
        BuildArtifactType.TestReport => "test_report",
        _ => "unknown"
    };

    public static BuildArtifactType ParseBuildArtifactType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "executable_binary" or "executablebinary" or "binary" or "exe" => BuildArtifactType.ExecutableBinary,
            "docker_image" or "dockerimage" or "image" => BuildArtifactType.DockerImage,
            "wasm_bundle" or "wasmbundle" or "wasm" => BuildArtifactType.WasmBundle,
            "nupkg" or "nugetpackage" or "nuget_package" => BuildArtifactType.NugetPackage,
            "npm_package" or "npmpackage" or "tgz" => BuildArtifactType.NpmPackage,
            "zip_archive" or "ziparchive" or "zip" => BuildArtifactType.ZipArchive,
            "test_report" or "testreport" or "report" => BuildArtifactType.TestReport,
            _ => BuildArtifactType.ExecutableBinary
        };

    public static string ToApiString(this ReleaseChannel value) => value switch
    {
        ReleaseChannel.Alpha => "alpha",
        ReleaseChannel.Beta => "beta",
        ReleaseChannel.ReleaseCandidate => "release_candidate",
        ReleaseChannel.Stable => "stable",
        ReleaseChannel.Nightly => "nightly",
        ReleaseChannel.Lts => "lts",
        _ => "unknown"
    };

    public static ReleaseChannel ParseReleaseChannel(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "alpha" => ReleaseChannel.Alpha,
            "beta" => ReleaseChannel.Beta,
            "release_candidate" or "releasecandidate" or "rc" => ReleaseChannel.ReleaseCandidate,
            "stable" => ReleaseChannel.Stable,
            "nightly" => ReleaseChannel.Nightly,
            "lts" => ReleaseChannel.Lts,
            _ => ReleaseChannel.Stable
        };

    public static string ToApiString(this ChangelogCategory value) => value switch
    {
        ChangelogCategory.Feature => "feature",
        ChangelogCategory.Fix => "fix",
        ChangelogCategory.Performance => "performance",
        ChangelogCategory.Documentation => "documentation",
        ChangelogCategory.Refactor => "refactor",
        ChangelogCategory.Deprecation => "deprecation",
        ChangelogCategory.Security => "security",
        ChangelogCategory.BreakingChange => "breaking_change",
        _ => "unknown"
    };

    public static ChangelogCategory ParseChangelogCategory(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "feature" or "feat" => ChangelogCategory.Feature,
            "fix" or "bugfix" => ChangelogCategory.Fix,
            "performance" or "perf" => ChangelogCategory.Performance,
            "documentation" or "docs" => ChangelogCategory.Documentation,
            "refactor" => ChangelogCategory.Refactor,
            "deprecation" or "deprecate" => ChangelogCategory.Deprecation,
            "security" or "sec" => ChangelogCategory.Security,
            "breaking_change" or "breakingchange" or "breaking" => ChangelogCategory.BreakingChange,
            _ => ChangelogCategory.Feature
        };

    public static string ToApiString(this SemVerBumpType value) => value switch
    {
        SemVerBumpType.Major => "major",
        SemVerBumpType.Minor => "minor",
        SemVerBumpType.Patch => "patch",
        SemVerBumpType.PreRelease => "prerelease",
        SemVerBumpType.BuildMetadata => "build_metadata",
        SemVerBumpType.None => "none",
        _ => "unknown"
    };

    public static SemVerBumpType ParseSemVerBumpType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "major" => SemVerBumpType.Major,
            "minor" => SemVerBumpType.Minor,
            "patch" => SemVerBumpType.Patch,
            "prerelease" or "pre" => SemVerBumpType.PreRelease,
            "build_metadata" or "buildmetadata" or "build" => SemVerBumpType.BuildMetadata,
            "none" => SemVerBumpType.None,
            _ => SemVerBumpType.Patch
        };

    public static string ToApiString(this LinterRuleSet value) => value switch
    {
        LinterRuleSet.Recommended => "recommended",
        LinterRuleSet.Strict => "strict",
        LinterRuleSet.SecurityFocused => "security_focused",
        LinterRuleSet.StyleOnly => "style_only",
        LinterRuleSet.Custom => "custom",
        LinterRuleSet.Disabled => "disabled",
        _ => "unknown"
    };

    public static LinterRuleSet ParseLinterRuleSet(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "recommended" or "default" => LinterRuleSet.Recommended,
            "strict" => LinterRuleSet.Strict,
            "security_focused" or "securityfocused" or "security" => LinterRuleSet.SecurityFocused,
            "style_only" or "styleonly" or "style" => LinterRuleSet.StyleOnly,
            "custom" => LinterRuleSet.Custom,
            "disabled" or "off" => LinterRuleSet.Disabled,
            _ => LinterRuleSet.Recommended
        };

    public static string ToApiString(this TestReportFormat value) => value switch
    {
        TestReportFormat.Trx => "trx",
        TestReportFormat.JunitXml => "junit_xml",
        TestReportFormat.NunitXml => "nunit_xml",
        TestReportFormat.HtmlSummary => "html_summary",
        TestReportFormat.CoberturaXml => "cobertura_xml",
        TestReportFormat.ConsoleOutput => "console_output",
        _ => "unknown"
    };

    public static TestReportFormat ParseTestReportFormat(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "trx" => TestReportFormat.Trx,
            "junit_xml" or "junitxml" or "junit" => TestReportFormat.JunitXml,
            "nunit_xml" or "nunitxml" or "nunit" => TestReportFormat.NunitXml,
            "html_summary" or "htmlsummary" or "html" => TestReportFormat.HtmlSummary,
            "cobertura_xml" or "coberturaxml" or "cobertura" => TestReportFormat.CoberturaXml,
            "console_output" or "consoleoutput" or "console" => TestReportFormat.ConsoleOutput,
            _ => TestReportFormat.Trx
        };

    public static string ToApiString(this GitCommitType value) => value switch
    {
        GitCommitType.Feat => "feat",
        GitCommitType.Fix => "fix",
        GitCommitType.Docs => "docs",
        GitCommitType.Style => "style",
        GitCommitType.Refactor => "refactor",
        GitCommitType.Perf => "perf",
        GitCommitType.Test => "test",
        GitCommitType.Chore => "chore",
        GitCommitType.Ci => "ci",
        GitCommitType.Build => "build",
        GitCommitType.Revert => "revert",
        _ => "unknown"
    };

    public static GitCommitType ParseGitCommitType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "feat" or "feature" => GitCommitType.Feat,
            "fix" or "bugfix" => GitCommitType.Fix,
            "docs" or "doc" => GitCommitType.Docs,
            "style" => GitCommitType.Style,
            "refactor" => GitCommitType.Refactor,
            "perf" or "performance" => GitCommitType.Perf,
            "test" or "tests" => GitCommitType.Test,
            "chore" => GitCommitType.Chore,
            "ci" => GitCommitType.Ci,
            "build" => GitCommitType.Build,
            "revert" => GitCommitType.Revert,
            _ => GitCommitType.Feat
        };

    public static string ToApiString(this DependencyUpdateStrategy value) => value switch
    {
        DependencyUpdateStrategy.LockfileOnly => "lockfile_only",
        DependencyUpdateStrategy.MinorAndPatch => "minor_and_patch",
        DependencyUpdateStrategy.PatchOnly => "patch_only",
        DependencyUpdateStrategy.LatestCompatible => "latest_compatible",
        DependencyUpdateStrategy.ManualApproval => "manual_approval",
        _ => "unknown"
    };

    public static DependencyUpdateStrategy ParseDependencyUpdateStrategy(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "lockfile_only" or "lockfileonly" => DependencyUpdateStrategy.LockfileOnly,
            "minor_and_patch" or "minorandpatch" => DependencyUpdateStrategy.MinorAndPatch,
            "patch_only" or "patchonly" => DependencyUpdateStrategy.PatchOnly,
            "latest_compatible" or "latestcompatible" => DependencyUpdateStrategy.LatestCompatible,
            "manual_approval" or "manualapproval" or "manual" => DependencyUpdateStrategy.ManualApproval,
            _ => DependencyUpdateStrategy.MinorAndPatch
        };

    public static string ToApiString(this BuildRunnerOS value) => value switch
    {
        BuildRunnerOS.UbuntuLatest => "ubuntu_latest",
        BuildRunnerOS.WindowsLatest => "windows_latest",
        BuildRunnerOS.MacOsLatest => "macos_latest",
        BuildRunnerOS.SelfHostedLinux => "self_hosted_linux",
        BuildRunnerOS.SelfHostedWindows => "self_hosted_windows",
        _ => "unknown"
    };

    public static BuildRunnerOS ParseBuildRunnerOS(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "ubuntu_latest" or "ubuntulatest" or "ubuntu" or "linux" => BuildRunnerOS.UbuntuLatest,
            "windows_latest" or "windowslatest" or "windows" or "win" => BuildRunnerOS.WindowsLatest,
            "macos_latest" or "macoslatest" or "macos" or "mac" => BuildRunnerOS.MacOsLatest,
            "self_hosted_linux" or "selfhostedlinux" => BuildRunnerOS.SelfHostedLinux,
            "self_hosted_windows" or "selfhostedwindows" => BuildRunnerOS.SelfHostedWindows,
            _ => BuildRunnerOS.UbuntuLatest
        };

    public static string ToApiString(this CodeQualityGateResult value) => value switch
    {
        CodeQualityGateResult.Passed => "passed",
        CodeQualityGateResult.Failed => "failed",
        CodeQualityGateResult.Warning => "warning",
        CodeQualityGateResult.Skipped => "skipped",
        CodeQualityGateResult.Pending => "pending",
        _ => "unknown"
    };

    public static CodeQualityGateResult ParseCodeQualityGateResult(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "passed" or "pass" or "success" => CodeQualityGateResult.Passed,
            "failed" or "fail" or "error" => CodeQualityGateResult.Failed,
            "warning" or "warn" => CodeQualityGateResult.Warning,
            "skipped" or "skip" => CodeQualityGateResult.Skipped,
            "pending" => CodeQualityGateResult.Pending,
            _ => CodeQualityGateResult.Passed
        };

    public static string ToApiString(this AgentWorkMode value) => value switch
    {
        AgentWorkMode.Autonomous => "autonomous",
        AgentWorkMode.SemiAutonomous => "semi_autonomous",
        AgentWorkMode.Supervised => "supervised",
        AgentWorkMode.Interactive => "interactive",
        AgentWorkMode.DryRun => "dry_run",
        _ => "unknown"
    };

    public static AgentWorkMode ParseAgentWorkMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "autonomous" or "auto" => AgentWorkMode.Autonomous,
            "semi_autonomous" or "semiautonomous" or "semi" => AgentWorkMode.SemiAutonomous,
            "supervised" => AgentWorkMode.Supervised,
            "interactive" => AgentWorkMode.Interactive,
            "dry_run" or "dryrun" => AgentWorkMode.DryRun,
            _ => AgentWorkMode.Autonomous
        };

    public static string ToApiString(this CodeReviewStatus value) => value switch
    {
        CodeReviewStatus.PendingReview => "pending_review",
        CodeReviewStatus.Approved => "approved",
        CodeReviewStatus.ChangesRequested => "changes_requested",
        CodeReviewStatus.Draft => "draft",
        CodeReviewStatus.ClosedUnmerged => "closed_unmerged",
        _ => "unknown"
    };

    public static CodeReviewStatus ParseCodeReviewStatus(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "pending_review" or "pendingreview" or "pending" => CodeReviewStatus.PendingReview,
            "approved" or "approve" => CodeReviewStatus.Approved,
            "changes_requested" or "changesrequested" => CodeReviewStatus.ChangesRequested,
            "draft" => CodeReviewStatus.Draft,
            "closed_unmerged" or "closedunmerged" or "closed" => CodeReviewStatus.ClosedUnmerged,
            _ => CodeReviewStatus.PendingReview
        };

    public static string ToApiString(this RepositoryLicense value) => value switch
    {
        RepositoryLicense.MIT => "mit",
        RepositoryLicense.Apache20 => "apache_2_0",
        RepositoryLicense.GPLv3 => "gpl_3_0",
        RepositoryLicense.BSD3Clause => "bsd_3_clause",
        RepositoryLicense.AGPLv3 => "agpl_3_0",
        RepositoryLicense.Proprietary => "proprietary",
        RepositoryLicense.Unlicense => "unlicense",
        _ => "unknown"
    };

    public static RepositoryLicense ParseRepositoryLicense(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "mit" => RepositoryLicense.MIT,
            "apache_2_0" or "apache20" or "apache-2.0" or "apache" => RepositoryLicense.Apache20,
            "gpl_3_0" or "gplv3" or "gpl-3.0" or "gpl" => RepositoryLicense.GPLv3,
            "bsd_3_clause" or "bsd3clause" or "bsd-3-clause" or "bsd" => RepositoryLicense.BSD3Clause,
            "agpl_3_0" or "agplv3" or "agpl-3.0" or "agpl" => RepositoryLicense.AGPLv3,
            "proprietary" or "commercial" => RepositoryLicense.Proprietary,
            "unlicense" or "public_domain" => RepositoryLicense.Unlicense,
            _ => RepositoryLicense.MIT
        };

    public static string ToApiString(this IssuePriorityLevel value) => value switch
    {
        IssuePriorityLevel.Low => "low",
        IssuePriorityLevel.Medium => "medium",
        IssuePriorityLevel.High => "high",
        IssuePriorityLevel.Urgent => "urgent",
        IssuePriorityLevel.Critical => "critical",
        _ => "unknown"
    };

    public static IssuePriorityLevel ParseIssuePriorityLevel(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "low" or "p3" => IssuePriorityLevel.Low,
            "medium" or "med" or "p2" => IssuePriorityLevel.Medium,
            "high" or "p1" => IssuePriorityLevel.High,
            "urgent" or "p0" => IssuePriorityLevel.Urgent,
            "critical" or "blocker" => IssuePriorityLevel.Critical,
            _ => IssuePriorityLevel.Medium
        };

    public static string ToApiString(this ProjectMilestoneStatus value) => value switch
    {
        ProjectMilestoneStatus.Planned => "planned",
        ProjectMilestoneStatus.InProgress => "in_progress",
        ProjectMilestoneStatus.FeatureComplete => "feature_complete",
        ProjectMilestoneStatus.InTesting => "in_testing",
        ProjectMilestoneStatus.Closed => "closed",
        ProjectMilestoneStatus.Overdue => "overdue",
        _ => "unknown"
    };

    public static ProjectMilestoneStatus ParseProjectMilestoneStatus(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "planned" => ProjectMilestoneStatus.Planned,
            "in_progress" or "inprogress" or "active" => ProjectMilestoneStatus.InProgress,
            "feature_complete" or "featurecomplete" => ProjectMilestoneStatus.FeatureComplete,
            "in_testing" or "intesting" or "testing" => ProjectMilestoneStatus.InTesting,
            "closed" or "completed" or "done" => ProjectMilestoneStatus.Closed,
            "overdue" or "late" => ProjectMilestoneStatus.Overdue,
            _ => ProjectMilestoneStatus.InProgress
        };
}
