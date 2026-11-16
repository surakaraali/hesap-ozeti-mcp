using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace HesapOzeti.Mcp;

// The API's response shapes (hesap-ozeti-api, Controllers/Contracts.cs). Only the fields this server uses.
public sealed record ApiAccount(int Id, string BankCode, string Iban, string Currency, string Name, DateTimeOffset? LastFetchedAt, int TransactionCount, int UnmatchedCount);

public sealed record ApiTransaction(
    long Id, int BankAccountId, DateTimeOffset BookedAt, decimal Amount, string Currency, string Description,
    string? CounterpartyName, string? CounterpartyIban, string? CounterpartyTaxNumber,
    string? CurrentAccountCode, string? CurrentAccountTitle, string? MatchedBy);

public sealed record ApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Reads from the Hesap Özeti REST API. It has GET methods only: this server cannot change anything,
/// and its token should be a viewer token, so the API would refuse a change even if a method for one were added.
/// </summary>
public sealed class HesapOzetiApi(HttpClient http, ILogger<HesapOzetiApi> logger)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The API's own maximum page size, used when this server reads every page itself.</summary>
    private const int ApiPageSize = 200;

    /// <summary>
    /// shortcut: a summary reads at most 10 pages (2,000 movements) and says so when it stops early;
    /// a company with more movements per quarter needs an aggregate endpoint in the API instead.
    /// </summary>
    public const int MaxPagesPerSummary = 10;

    public Task<List<ApiAccount>> GetAccountsAsync(CancellationToken cancellationToken) =>
        GetAsync<List<ApiAccount>>("api/accounts", cancellationToken);

    public Task<ApiPage<ApiTransaction>> GetTransactionsAsync(
        DateRange range, int? accountId, bool? matched, int page, int pageSize, CancellationToken cancellationToken)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"api/transactions?from={range.From:yyyy-MM-dd}&to={range.To:yyyy-MM-dd}&page={page}&pageSize={pageSize}");
        if (accountId is { } id)
        {
            url += string.Create(CultureInfo.InvariantCulture, $"&accountId={id}");
        }

        if (matched is { } m)
        {
            url += m ? "&matched=true" : "&matched=false";
        }

        return GetAsync<ApiPage<ApiTransaction>>(url, cancellationToken);
    }

    /// <summary>Every movement in the range, for summaries. Truncated is true when the page limit stopped the reading.</summary>
    public async Task<(List<ApiTransaction> Items, bool Truncated)> GetAllTransactionsAsync(
        DateRange range, int? accountId, bool? matched, CancellationToken cancellationToken)
    {
        var items = new List<ApiTransaction>();
        for (var page = 1; page <= MaxPagesPerSummary; page++)
        {
            var result = await GetTransactionsAsync(range, accountId, matched, page, ApiPageSize, cancellationToken);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0)
            {
                return (items, false);
            }
        }

        return (items, true);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, cancellationToken);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. A cancellation the caller asked for is passed on as it is.
            logger.LogWarning("Hesap Özeti API did not answer within {Seconds} s", Timeout.TotalSeconds);
            throw new McpException(ApiErrors.Timeout, e);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("Hesap Özeti API is unreachable: {Error}", e.HttpRequestError);
            throw new McpException(ApiErrors.Unreachable, e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The status code only. The body can echo the request, and the request never reaches a log.
                logger.LogWarning("Hesap Özeti API answered {Status}", (int)response.StatusCode);
                throw new McpException(ApiErrors.ForStatus(response.StatusCode));
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, cancellationToken)
                    ?? throw new McpException(ApiErrors.UnexpectedBody);
            }
            catch (JsonException e)
            {
                throw new McpException(ApiErrors.UnexpectedBody, e);
            }
        }
    }
}

/// <summary>
/// What the model is told when the API fails. Each message says what happened, whether retrying helps, and not to
/// invent an answer: a model that receives an empty list instead of an error reports "no unmatched collections".
/// </summary>
public static class ApiErrors
{
    private const string DoNotGuess = "Veri alınamadı; tahmini bir cevap verme, durumu kullanıcıya aktar.";

    public static readonly string Timeout =
        $"Hesap Özeti API {HesapOzetiApi.Timeout.TotalSeconds:0} saniye içinde yanıt vermedi. Bir kez daha denenebilir; yine olursa {DoNotGuess}";

    public const string Unreachable =
        "Hesap Özeti API'ye bağlanılamadı (çalışmıyor ya da HESAP_OZETI_API_URL yanlış). Tekrar denemek işe yaramaz; " + DoNotGuess;

    public const string UnexpectedBody = "Hesap Özeti API beklenmeyen bir yanıt döndürdü. " + DoNotGuess;

    public static string ForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized =>
            "API token'ı geçersiz ya da süresi dolmuş (401). Kullanıcı yeni bir token alıp HESAP_OZETI_TOKEN ortam değişkenine "
            + "koymalı ve MCP sunucusunu yeniden başlatmalı. Tekrar denemek işe yaramaz; " + DoNotGuess,
        HttpStatusCode.Forbidden =>
            "Bu token'ın bu veriyi okuma yetkisi yok (403). Kullanıcı yetkili bir token almalı. " + DoNotGuess,
        HttpStatusCode.NotFound =>
            "API bu adresi bulamadı (404). HESAP_OZETI_API_URL yanlış olabilir. " + DoNotGuess,
        HttpStatusCode.BadRequest =>
            "API isteği geçersiz buldu (400). Tarih ve id değerlerini kontrol edip bir kez daha dene.",
        HttpStatusCode.TooManyRequests =>
            "API çok fazla istek aldı (429). Biraz bekleyip tekrar dene.",
        >= HttpStatusCode.InternalServerError =>
            $"Hesap Özeti API'de sunucu hatası oluştu ({(int)status}). Bir kez daha denenebilir; yine olursa {DoNotGuess}",
        _ => $"Hesap Özeti API beklenmeyen bir durum kodu döndürdü ({(int)status}). {DoNotGuess}",
    };
}
