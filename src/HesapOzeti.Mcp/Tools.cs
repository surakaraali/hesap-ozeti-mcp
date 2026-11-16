using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;

namespace HesapOzeti.Mcp;

// What the model receives. Masked numbers, Turkish local times, a direction word next to the signed amount:
// the model should not have to do time zone arithmetic or remember which sign means what.

public sealed record AccountView(int Id, string Name, string Bank, string? Iban, string Currency, int TransactionCount, int UnmatchedCount, string? LastFetchedAt);

public sealed record AccountsResult(string Today, IReadOnlyList<AccountView> Accounts);

public sealed record TransactionView(
    long Id, int AccountId, string BookedAt, decimal Amount, string Currency, string Direction, string Description,
    string? CounterpartyName, string? CounterpartyIban, string? CounterpartyTaxNumber,
    string? CurrentAccountCode, string? CurrentAccountTitle, string? MatchedBy);

public sealed record TransactionsResult(string From, string To, int Page, int PageSize, int TotalCount, IReadOnlyList<TransactionView> Items);

public sealed record CurrencyTotal(string Currency, decimal Incoming, decimal Outgoing, int Count);

public sealed record UnmatchedResult(
    string From, string To, string Direction, int Count, IReadOnlyList<CurrencyTotal> Totals,
    bool Truncated, string? Note, IReadOnlyList<TransactionView> Items);

public sealed record CurrentAccountTotal(string Code, string Title, string Currency, decimal Incoming, decimal Outgoing, int Count);

public sealed record SummaryResult(
    string From, string To, string? Filter, IReadOnlyList<CurrentAccountTotal> CurrentAccounts,
    IReadOnlyList<CurrencyTotal> Unmatched, IReadOnlyList<TransactionView> PossibleUnmatched, bool Truncated, string? Note);

/// <summary>
/// The tools. All of them only read, and say so to the client (ReadOnly = true), so a client may run them without
/// asking the user each time. There is deliberately no tool that matches, pays or deletes anything.
/// </summary>
[McpServerToolType]
public static class Tools
{
    private const string FromDescription = "İlk gün, yyyy-MM-dd, Türkiye saatine göre, dahil.";
    private const string ToDescription = "Son gün, yyyy-MM-dd, Türkiye saatine göre, dahil. Aralık en fazla 92 gün.";
    private const string AccountIdDescription = "İsteğe bağlı. Tek bir banka hesabı için list_accounts'tan gelen id.";
    private const int MaxListed = 100;
    private const int MaxPossibleUnmatched = 20;

    [McpServerTool(Name = "list_accounts", Title = "Banka hesapları", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Şirketin banka hesaplarını listeler: id, banka, maskeli IBAN, para birimi, toplam hareket sayısı ve henüz bir cariye "
        + "eşleşmemiş hareket sayısı. Ayrıca Türkiye saatine göre bugünün tarihini (today) döner: 'dün', 'bu hafta', 'bu ay' gibi "
        + "ifadeleri tarihe çevirmen gerekiyorsa ve bugünü bilmiyorsan önce bunu çağır. Diğer tool'lardaki accountId buradaki id'dir.")]
    public static async Task<AccountsResult> ListAccounts(HesapOzetiApi api, TimeProvider time, CancellationToken cancellationToken)
    {
        var accounts = await api.GetAccountsAsync(cancellationToken);
        return new(
            TurkeyTime.Today(time).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [.. accounts.Select(a => new AccountView(
                a.Id, a.Name, a.BankCode, Masking.Iban(a.Iban), a.Currency, a.TransactionCount, a.UnmatchedCount,
                a.LastFetchedAt is { } at ? TurkeyTime.Format(at) : null))]);
    }

    [McpServerTool(Name = "list_transactions", Title = "Banka hareketleri", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Bir tarih aralığındaki banka hareketlerini (tahsilat ve ödemeler) en yeniden eskiye, sayfa sayfa listeler. Tutar pozitifse "
        + "tahsilat (para girişi), negatifse ödemedir. Bir günün dökümünü ya da tek tek hareketleri görmek için kullan. "
        + "Eşleşmeyen hareketler için list_unmatched_transactions, bir cariden toplam ne geldiği için summarize_by_current_account "
        + "daha doğru ve ucuzdur; toplam hesaplamak için bu tool'u sayfa sayfa gezme.")]
    public static async Task<TransactionsResult> ListTransactions(
        HesapOzetiApi api,
        [Description(FromDescription)] string from,
        [Description(ToDescription)] string to,
        [Description(AccountIdDescription)] int? accountId = null,
        [Description("Sayfa numarası, 1'den başlar.")] int page = 1,
        [Description("Sayfadaki hareket sayısı, en fazla 100.")] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var range = DateRange.Parse(from, to);
        Input.Paging(page, pageSize);
        var result = await api.GetTransactionsAsync(range, Input.AccountId(accountId), null, page, pageSize, cancellationToken);
        return new(from, to, result.Page, result.PageSize, result.TotalCount, [.. result.Items.Select(ToView)]);
    }

    [McpServerTool(Name = "list_unmatched_transactions", Title = "Eşleşmeyen hareketler", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Henüz bir cari hesaba (ERP'deki müşteri ya da tedarikçi kartı) eşleşmemiş banka hareketlerini, sayısını ve para birimi "
        + "bazında toplamını getirir. 'Eşleşmeyen tahsilatlar', 'kimden geldiği belli olmayan paralar', 'mutabakat bekleyenler' "
        + "soruları için bunu kullan. direction varsayılan olarak incoming (tahsilat, para girişi); ödemeler için outgoing, "
        + "ikisi için all. En fazla 100 hareket listelenir, sayı ve toplamlar ise tüm aralık içindir.")]
    public static async Task<UnmatchedResult> ListUnmatchedTransactions(
        HesapOzetiApi api,
        [Description(FromDescription)] string from,
        [Description(ToDescription)] string to,
        [Description("incoming (tahsilat, varsayılan), outgoing (ödeme) ya da all.")] string direction = "incoming",
        [Description(AccountIdDescription)] int? accountId = null,
        CancellationToken cancellationToken = default)
    {
        var range = DateRange.Parse(from, to);
        var wanted = Input.ParseDirection(direction);
        var (all, truncated) = await api.GetAllTransactionsAsync(range, Input.AccountId(accountId), matched: false, cancellationToken);

        var items = all.Where(t => wanted switch
        {
            Direction.Incoming => t.Amount > 0,
            Direction.Outgoing => t.Amount < 0,
            _ => true,
        }).ToList();

        return new(
            from, to, wanted.ToString().ToLowerInvariant(), items.Count, Totals(items), truncated,
            Note(truncated, items.Count > MaxListed ? $"{items.Count} hareketin en yeni {MaxListed} tanesi listelendi." : null),
            [.. items.Take(MaxListed).Select(ToView)]);
    }

    [McpServerTool(Name = "summarize_by_current_account", Title = "Cari bazında özet", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Bir tarih aralığındaki hareketleri cari hesap bazında toplar: her cari için gelen (tahsilat), giden (ödeme) toplam ve "
        + "hareket sayısı. 'X carisinden bu ay ne kadar geldi?', 'en çok tahsilat yapan müşteriler' soruları için kullan. "
        + "currentAccount verilirse cari kodu (örnek 120.01.001, baştan eşleşir) ya da unvanın bir parçası ile süzer. "
        + "Eşleşmemiş hareketler hiçbir cariye sayılmaz: toplamları 'unmatched' altında, isim olarak aramaya benzeyenler "
        + "'possibleUnmatched' altında gelir. Bir carinin toplamını söylerken bunlar varsa ayrıca belirt.")]
    public static async Task<SummaryResult> SummarizeByCurrentAccount(
        HesapOzetiApi api,
        [Description(FromDescription)] string from,
        [Description(ToDescription)] string to,
        [Description("İsteğe bağlı. Cari kodu ya da unvanın bir parçası, örnek '120.01' veya 'kuzey lojistik'.")] string? currentAccount = null,
        CancellationToken cancellationToken = default)
    {
        var range = DateRange.Parse(from, to);
        var search = Input.Search(currentAccount);
        var (all, truncated) = await api.GetAllTransactionsAsync(range, null, null, cancellationToken);

        var matched = all.Where(t => t.CurrentAccountCode is not null
            && (search is null || t.CurrentAccountCode.StartsWith(search, StringComparison.OrdinalIgnoreCase) || Contains(t.CurrentAccountTitle, search)));
        var unmatched = all.Where(t => t.CurrentAccountCode is null).ToList();

        var totals = matched
            .GroupBy(t => (t.CurrentAccountCode!, t.CurrentAccountTitle ?? "", t.Currency))
            .Select(g => new CurrentAccountTotal(
                g.Key.Item1, g.Key.Item2, g.Key.Currency,
                g.Where(t => t.Amount > 0).Sum(t => t.Amount), g.Where(t => t.Amount < 0).Sum(t => t.Amount), g.Count()))
            .OrderByDescending(c => c.Incoming)
            .ToList();

        var possible = search is null ? [] : unmatched.Where(t => Contains(t.CounterpartyName, search)).Take(MaxPossibleUnmatched).Select(ToView).ToList();

        return new(from, to, search, totals, Totals(unmatched), possible, truncated, Note(truncated, null));
    }

    private static TransactionView ToView(ApiTransaction t) => new(
        t.Id, t.BankAccountId, TurkeyTime.Format(t.BookedAt), t.Amount, t.Currency,
        t.Amount >= 0 ? "tahsilat" : "ödeme",
        Masking.Text(t.Description),
        t.CounterpartyName is null ? null : Masking.Text(t.CounterpartyName, 120),
        Masking.Iban(t.CounterpartyIban),
        Masking.TaxNumber(t.CounterpartyTaxNumber),
        t.CurrentAccountCode, t.CurrentAccountTitle, t.MatchedBy);

    private static List<CurrencyTotal> Totals(IEnumerable<ApiTransaction> items) =>
        [.. items.GroupBy(t => t.Currency).Select(g => new CurrencyTotal(
            g.Key, g.Where(t => t.Amount > 0).Sum(t => t.Amount), g.Where(t => t.Amount < 0).Sum(t => t.Amount), g.Count()))];

    private static string? Note(bool truncated, string? other) => truncated
        ? $"Aralıkta {HesapOzetiApi.MaxPagesPerSummary * 200} hareketten fazlası var; toplamlar eksik. Kullanıcıya söyle ve daha kısa bir aralık öner."
        : other;

    // Turkish rules for case ("i" and "İ") and ignoring accents, so "kuzey lojistik" finds "KUZEY LOJİSTİK A.Ş.".
    private static readonly CompareInfo Turkish = CultureInfo.GetCultureInfo("tr-TR").CompareInfo;

    private static bool Contains(string? text, string search) =>
        text is not null && Turkish.IndexOf(text, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}

public static class TurkeyTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime);

    public static string Format(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
