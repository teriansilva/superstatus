using System.Net;
using System.Text;
using SuperStatus.Web;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #105 (UI slice): StatusApiClient operator-action helpers map the
/// endpoint contract — 404 → null (check gone), 200 → parsed result —
/// without needing a live API.
/// </summary>
[TestClass]
public class StatusApiClientOperatorActionsTests
{
    private static StatusApiClient Client(HttpStatusCode code, string? json)
        => new(new HttpClient(new StubHandler(code, json)) { BaseAddress = new Uri("http://api.test") });

    [TestMethod]
    public async Task RunCheckNow_200_ReturnsParsedTick()
    {
        var json = """{"id":5,"httpStatusCode":200,"responseTimeInMs":82,"checkFailed":false}""";
        var client = Client(HttpStatusCode.OK, json);

        var result = await client.RunCheckNowAsync(5);

        Assert.IsNotNull(result);
        Assert.AreEqual(200, result!.HttpStatusCode);
        Assert.AreEqual(82, result.ResponseTimeInMs);
        Assert.IsFalse(result.CheckFailed);
    }

    [TestMethod]
    public async Task RunCheckNow_404_ReturnsNull()
    {
        var client = Client(HttpStatusCode.NotFound, null);
        Assert.IsNull(await client.RunCheckNowAsync(999));
    }

    [TestMethod]
    public async Task SetEnabled_200_ReturnsNewState()
    {
        var client = Client(HttpStatusCode.OK, """{"id":5,"enabled":false}""");
        var result = await client.SetEnabledAsync(5, false);
        Assert.AreEqual(false, result);
    }

    [TestMethod]
    public async Task SetEnabled_404_ReturnsNull()
    {
        var client = Client(HttpStatusCode.NotFound, null);
        Assert.IsNull(await client.SetEnabledAsync(999, true));
    }

    [TestMethod]
    public async Task RunCheckNow_500_Throws()
    {
        var client = Client(HttpStatusCode.InternalServerError, null);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => client.RunCheckNowAsync(5));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string? _json;
        public StubHandler(HttpStatusCode code, string? json) { _code = code; _json = json; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(_code);
            if (_json is not null) resp.Content = new StringContent(_json, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }
}
