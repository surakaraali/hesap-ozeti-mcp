using System.Globalization;
using ModelContextProtocol;

namespace HesapOzeti.Mcp;

// Tool arguments come from the model, so they are checked like any other input from outside.
// A refusal is an McpException: its message reaches the model, which can then correct the call by itself.

/// <summary>A range of Turkish days, both ends included.</summary>
public readonly record struct DateRange(DateOnly From, DateOnly To)
{
    /// <summary>A quarter. Longer ranges make the API read many pages and fill the model's context with rows nobody reads.</summary>
    public const int MaxDays = 92;

    public static DateRange Parse(string? from, string? to)
    {
        var start = ParseDay(from, "from");
        var end = ParseDay(to, "to");

        if (start > end)
        {
            throw new McpException($"'from' ({from}) 'to' ({to}) tarihinden sonra olamaz.");
        }

        if (end.DayNumber - start.DayNumber + 1 > MaxDays)
        {
            throw new McpException($"Tarih aralığı en fazla {MaxDays} gün olabilir. Daha uzun bir dönem için aralığı parçalara böl.");
        }

        return new(start, end);
    }

    // Only yyyy-MM-dd. "12.11.2026" is 12 November to a Turkish user and 11 December to the invariant culture; refusing beats guessing.
    private static DateOnly ParseDay(string? value, string name) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new McpException($"'{name}' yyyy-MM-dd biçiminde bir tarih olmalı (örnek: 2026-11-15). Gelen değer: '{value}'.");
}

public static class Input
{
    /// <summary>Lower than the API's 200: each row costs the model context, and 100 rows is more than anyone reads in a chat.</summary>
    public const int MaxPageSize = 100;

    public const int MaxPage = 1000;

    public const int MaxSearchLength = 100;

    public static void Paging(int page, int pageSize)
    {
        if (page is < 1 or > MaxPage)
        {
            throw new McpException($"'page' 1 ile {MaxPage} arasında olmalı.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new McpException($"'pageSize' 1 ile {MaxPageSize} arasında olmalı.");
        }
    }

    public static int? AccountId(int? accountId) =>
        accountId is null or > 0
            ? accountId
            : throw new McpException("'accountId' pozitif bir sayı olmalı. Geçerli id'ler için list_accounts tool'unu çağır.");

    public static Direction ParseDirection(string? direction) => direction?.Trim().ToLowerInvariant() switch
    {
        null or "" or "incoming" => Direction.Incoming,
        "outgoing" => Direction.Outgoing,
        "all" => Direction.All,
        _ => throw new McpException($"'direction' incoming, outgoing ya da all olmalı. Gelen değer: '{direction}'."),
    };

    public static string? Search(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxSearchLength
            ? trimmed
            : throw new McpException($"'currentAccount' en fazla {MaxSearchLength} karakter olabilir.");
    }
}

public enum Direction
{
    Incoming,
    Outgoing,
    All,
}
