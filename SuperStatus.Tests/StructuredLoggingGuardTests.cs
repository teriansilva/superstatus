using System.Text.RegularExpressions;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #85 — locks in the structured-logging cleanup. The scoped status-check
/// paths must never reintroduce an interpolated ($"...") logging template
/// (CA2254), and the repo-level analyzer guard must stay enabled. The analyzer
/// is the live guard; this test makes "the grep returns nothing" a CI-enforced
/// assertion (Hermes review acceptance) so a regression fails the build even if
/// CA2254 warnings are ever silenced.
/// </summary>
[TestClass]
public class StructuredLoggingGuardTests
{
    // Mirrors the acceptance grep: any logger.LogXxx( whose first argument is
    // an interpolated string.
    private static readonly Regex InterpolatedLogCall =
        new(@"Log[A-Za-z]+\(\s*\$""", RegexOptions.Compiled);

    private static readonly string[] ScopedSources =
    [
        "SuperStatus.Services/Services/StatusCheckService.cs",
        "SuperStatus.ApiService/Jobs/SuperStatusJob.cs",
        "SuperStatus.ApiService/Jobs/SuperStatusCleanUpJob.cs",
    ];

    [TestMethod]
    public void ScopedStatusCheckPaths_HaveNoInterpolatedLogTemplates()
    {
        string root = RepoRoot();
        foreach (var rel in ScopedSources)
        {
            string path = Path.Combine(root, rel);
            Assert.IsTrue(File.Exists(path), $"Scoped source not found (anchor broke?): {path}");
            string src = File.ReadAllText(path);
            var hits = InterpolatedLogCall.Matches(src);
            Assert.AreEqual(0, hits.Count,
                $"{rel} reintroduced an interpolated log template (CA2254). Use logger.LogX(\"... {{Field}}\", value).");
        }
    }

    [TestMethod]
    public void EditorConfig_EnablesCa2254Guard()
    {
        string editorConfig = Path.Combine(RepoRoot(), ".editorconfig");
        Assert.IsTrue(File.Exists(editorConfig), "Root .editorconfig is missing.");
        StringAssert.Contains(File.ReadAllText(editorConfig), "dotnet_diagnostic.CA2254.severity",
            "The CA2254 structured-logging guard must stay configured in .editorconfig.");
    }

    /// <summary>Walk up from the test assembly to the repo root (the directory
    /// holding SuperStatus.sln). Fails loudly rather than silently passing if
    /// the layout ever changes.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SuperStatus.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root (SuperStatus.sln) from " + AppContext.BaseDirectory);
    }
}
