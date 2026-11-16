using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HesapOzeti.Mcp.Tests;

/// <summary>Answers every request with whatever the test hands it, and remembers the requests.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return send(request, cancellationToken);
    }

    public static HesapOzetiApi Api(StubHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080/"), Timeout = timeout ?? HesapOzetiApi.Timeout },
            NullLogger<HesapOzetiApi>.Instance);

    public static StubHandler Json(string json) => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
}

public class ApiErrorTests
{
    private static readonly DateRange November = DateRange.Parse("2026-11-01", "2026-11-15");

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    public async Task An_error_status_reaches_the_model_as_a_message(HttpStatusCode status, string code)
    {
        var api = StubHandler.Api(new((_, _) => Task.FromResult(new HttpResponseMessage(status))));

        var error = await Assert.ThrowsAsync<McpException>(() => api.GetAccountsAsync(CancellationToken.None));

        Assert.Contains(code, error.Message);
    }

    [Fact]
    public async Task Expired_token_tells_the_user_what_to_do()
    {
        var api = StubHandler.Api(new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        var error = await Assert.ThrowsAsync<McpException>(() => api.GetTransactionsAsync(November, null, null, 1, 50, CancellationToken.None));

        Assert.Contains("HESAP_OZETI_TOKEN", error.Message);
        Assert.Contains("tahmini bir cevap verme", error.Message);
    }

    [Fact]
    public async Task A_timeout_is_reported_as_a_timeout()
    {
        var api = StubHandler.Api(
            new(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            timeout: TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<McpException>(() => api.GetAccountsAsync(CancellationToken.None));

        Assert.Equal(ApiErrors.Timeout, error.Message);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_is_not_called_a_timeout()
    {
        using var cancel = new CancellationTokenSource();
        var api = StubHandler.Api(new(async (_, ct) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetAccountsAsync(cancel.Token));
    }

    [Fact]
    public async Task An_unreachable_api_is_reported()
    {
        var api = StubHandler.Api(new((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused")));

        var error = await Assert.ThrowsAsync<McpException>(() => api.GetAccountsAsync(CancellationToken.None));

        Assert.Equal(ApiErrors.Unreachable, error.Message);
    }

    [Fact]
    public async Task A_body_that_is_not_the_expected_json_is_reported()
    {
        var api = StubHandler.Api(StubHandler.Json("<html>login</html>"));

        var error = await Assert.ThrowsAsync<McpException>(() => api.GetAccountsAsync(CancellationToken.None));

        Assert.Equal(ApiErrors.UnexpectedBody, error.Message);
    }
}

public class ToolTests
{
    private static readonly string Page = """
        {"items":[
          {"id":1,"bankAccountId":1,"bookedAt":"2026-11-16T09:16:05+00:00","amount":3200.00,"currency":"TRY","description":"HAVALE GELEN VKN 3456789012",
           "counterpartyName":"KUZEY LOJİSTİK A.Ş.","counterpartyIban":"TR56 0006 4099 9900 0055 5566 66","counterpartyTaxNumber":"3456789012",
           "currentAccountCode":null,"currentAccountTitle":null,"matchedBy":null},
          {"id":2,"bankAccountId":1,"bookedAt":"2026-11-16T07:00:00+00:00","amount":-15000.00,"currency":"TRY","description":"KİRA ÖDEMESİ",
           "counterpartyName":"IŞIK GAYRİMENKUL","counterpartyIban":null,"counterpartyTaxNumber":null,
           "currentAccountCode":null,"currentAccountTitle":null,"matchedBy":null}
          {MATCHED}
        ],"page":1,"pageSize":200,"totalCount":3}
        """.Replace("{MATCHED}", Matched, StringComparison.Ordinal);

    private const string Matched = """
        ,{"id":3,"bankAccountId":2,"bookedAt":"2026-11-13T07:45:00+00:00","amount":84999.99,"currency":"TRY","description":"TOPLU TAHSILAT",
           "counterpartyName":"ABC TEKSTİL SANAYİ A.Ş.","counterpartyIban":"TR340004605555000011112222","counterpartyTaxNumber":"1234567890",
           "currentAccountCode":"120.01.001","currentAccountTitle":"ABC TEKSTİL SANAYİ A.Ş.","matchedBy":"Iban"}
        """;

    [Fact]
    public async Task Unmatched_collections_are_masked_and_filtered_to_money_coming_in()
    {
        // The API returns unmatched movements only (matched=false); the tool keeps the incoming ones.
        var handler = StubHandler.Json(Page.Replace(Matched, "", StringComparison.Ordinal).Replace("\"totalCount\":3", "\"totalCount\":2", StringComparison.Ordinal));

        var result = await Tools.ListUnmatchedTransactions(StubHandler.Api(handler), "2026-11-01", "2026-11-16");

        var item = Assert.Single(result.Items);
        Assert.Equal(3200.00m, item.Amount);
        Assert.Equal("TR56 **** 6666", item.CounterpartyIban);
        Assert.Equal("*******012", item.CounterpartyTaxNumber);
        Assert.Equal("HAVALE GELEN VKN *******012", item.Description);
        Assert.Equal("2026-11-16 12:16", item.BookedAt); // Turkish time, UTC+3
        Assert.Contains("matched=false", handler.Requests.Single().RequestUri!.Query);
    }

    [Fact]
    public async Task Summary_finds_a_current_account_by_part_of_its_title_in_turkish()
    {
        var result = await Tools.SummarizeByCurrentAccount(StubHandler.Api(StubHandler.Json(Page)), "2026-11-01", "2026-11-16", "abc tekstil");

        var abc = Assert.Single(result.CurrentAccounts);
        Assert.Equal("120.01.001", abc.Code);
        Assert.Equal(84999.99m, abc.Incoming);
        Assert.Empty(result.PossibleUnmatched);
    }

    [Fact]
    public async Task Summary_points_at_unmatched_movements_with_a_similar_name()
    {
        var result = await Tools.SummarizeByCurrentAccount(StubHandler.Api(StubHandler.Json(Page)), "2026-11-01", "2026-11-16", "kuzey lojistik");

        Assert.Empty(result.CurrentAccounts);
        Assert.Equal(1, Assert.Single(result.PossibleUnmatched).Id);
        Assert.Equal(2, Assert.Single(result.Unmatched).Count);
    }

    [Fact]
    public async Task Invalid_input_never_reaches_the_api()
    {
        var handler = StubHandler.Json(Page);

        await Assert.ThrowsAsync<McpException>(() => Tools.ListTransactions(StubHandler.Api(handler), "2026-01-01", "2026-12-31"));
        await Assert.ThrowsAsync<McpException>(() => Tools.ListTransactions(StubHandler.Api(handler), "2026-11-01", "2026-11-02", pageSize: 500));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Every_tool_is_read_only()
    {
        var tools = typeof(Tools).GetMethods().Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).OfType<McpServerToolAttribute>().ToList();

        Assert.Equal(4, tools.Count);
        Assert.All(tools, t => Assert.True(t.ReadOnly && !t.Destructive, t.Name));
    }

    [Fact]
    public void The_api_client_only_reads() =>
        Assert.DoesNotContain(typeof(HesapOzetiApi).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            m => !m.Name.StartsWith("Get", StringComparison.Ordinal));
}

public class SettingsTests
{
    [Fact]
    public void Missing_token_stops_the_server()
    {
        var settings = Settings.FromEnvironment(_ => null);

        Assert.NotNull(settings.Error);
        Assert.Contains(Settings.TokenVariable, settings.Error);
    }

    [Fact]
    public void Defaults_to_the_local_api() =>
        Assert.Equal(new Uri("http://localhost:8080/"), Settings.FromEnvironment(n => n == Settings.TokenVariable ? "t" : null).ApiUrl);

    [Theory]
    [InlineData("http://api.example.com", false)] // the token would cross the network unencrypted
    [InlineData("https://api.example.com", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("ftp://localhost", false)]
    public void Remote_apis_need_https(string url, bool accepted)
    {
        var settings = Settings.FromEnvironment(n => n == Settings.TokenVariable ? "secret-token" : url);

        Assert.Equal(accepted, settings.Error is null);
        Assert.DoesNotContain("secret-token", settings.Error ?? "");
    }
}
