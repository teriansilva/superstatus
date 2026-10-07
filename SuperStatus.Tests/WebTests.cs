namespace SuperStatus.Tests;

[TestClass]
public class WebTests
{
    // Spins up the entire Aspire AppHost (Postgres, Identity, API, Web) via
    // DistributedApplicationTestingBuilder, which requires a Docker runtime
    // on the test host. The PR validation runner (a Forgejo Actions self-
    // hosted runner job container) does not provide docker-in-docker, so we
    // tag the test and filter it out in .forgejo/workflows/validate.yml with
    // `--filter "TestCategory!=RequiresDocker"`. Runs locally as long as
    // Docker is available.
    [TestMethod]
    [TestCategory("RequiresDocker")]
    public async Task GetWebResourceRootReturnsOkStatusCode()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SuperStatus_AppHost>();
        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        await using var app = await appHost.BuildAsync();
        var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await app.StartAsync();

        // Act
        var httpClient = app.CreateHttpClient("webfrontend");
        await resourceNotificationService.WaitForResourceAsync("webfrontend", KnownResourceStates.Running).WaitAsync(TimeSpan.FromSeconds(30));
        var response = await httpClient.GetAsync("/");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
