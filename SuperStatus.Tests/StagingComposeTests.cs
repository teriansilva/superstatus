namespace SuperStatus.Tests;

/// <summary>
/// Issue #369 — staging runs behind the public reverse proxy. Web must receive
/// WEBAPP_HTTP as well as Identity; otherwise it enters the self-host two-port
/// mode and rewrites sign-in redirects to the web host on :8081.
/// </summary>
[TestClass]
public class StagingComposeTests
{
    [TestMethod]
    public void WebReceivesWebAppHttp_ToDisableSelfHostIdentityPortRedirects()
    {
        var lines = File.ReadAllLines(FindRepoFile("docker-compose.staging.yml"));
        var web = ExtractService(lines, "web");
        var identity = ExtractService(lines, "identity");

        CollectionAssert.Contains(identity, "      WEBAPP_HTTP: ${WEBAPP_HTTP}");
        CollectionAssert.Contains(web, "      IDP_HTTP: ${IDP_HTTP}");
        CollectionAssert.Contains(web, "      WEBAPP_HTTP: ${WEBAPP_HTTP}");
    }

    private static string FindRepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {name} from {AppContext.BaseDirectory}.");
    }

    private static List<string> ExtractService(string[] lines, string serviceName)
    {
        var start = Array.FindIndex(lines, line => line == $"  {serviceName}:");
        if (start < 0)
        {
            Assert.Fail($"Service '{serviceName}' not found in docker-compose.staging.yml.");
        }

        var result = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("  ", StringComparison.Ordinal)
                && !line.StartsWith("    ", StringComparison.Ordinal)
                && line.EndsWith(":", StringComparison.Ordinal))
            {
                break;
            }

            result.Add(line);
        }

        return result;
    }
}
