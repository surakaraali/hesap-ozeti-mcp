using ModelContextProtocol;

namespace HesapOzeti.Mcp.Tests;

public class InputTests
{
    [Fact]
    public void A_quarter_is_accepted()
    {
        var range = DateRange.Parse("2026-09-01", "2026-11-30"); // 91 days

        Assert.Equal(new DateOnly(2026, 9, 1), range.From);
        Assert.Equal(new DateOnly(2026, 11, 30), range.To);
    }

    [Fact]
    public void One_day_is_accepted() =>
        Assert.Equal(new DateOnly(2026, 11, 15), DateRange.Parse("2026-11-15", "2026-11-15").To);

    [Theory]
    [InlineData("15.11.2026", "2026-11-15")] // Turkish writing: refused, not guessed
    [InlineData("2026-11-15", "yesterday")]
    [InlineData("", "2026-11-15")]
    [InlineData(null, "2026-11-15")]
    [InlineData("2026-02-30", "2026-03-01")]
    public void Dates_must_be_iso(string? from, string to)
    {
        var error = Assert.Throws<McpException>(() => DateRange.Parse(from, to));
        Assert.Contains("yyyy-MM-dd", error.Message);
    }

    [Fact]
    public void From_after_to_is_refused() =>
        Assert.Throws<McpException>(() => DateRange.Parse("2026-11-15", "2026-11-01"));

    [Fact]
    public void Ranges_longer_than_92_days_are_refused()
    {
        Assert.Equal(92, DateRange.Parse("2026-01-01", "2026-04-02").To.DayNumber - new DateOnly(2026, 1, 1).DayNumber + 1);
        var error = Assert.Throws<McpException>(() => DateRange.Parse("2026-01-01", "2026-04-03"));
        Assert.Contains("92", error.Message);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 100)]
    [InlineData(1000, 50)]
    public void Paging_within_limits_is_accepted(int page, int pageSize) => Input.Paging(page, pageSize);

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1001, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(1, 10_000)]
    public void Paging_outside_limits_is_refused(int page, int pageSize) =>
        Assert.Throws<McpException>(() => Input.Paging(page, pageSize));

    [Fact]
    public void Account_id_must_be_positive()
    {
        Assert.Null(Input.AccountId(null));
        Assert.Equal(2, Input.AccountId(2));
        Assert.Throws<McpException>(() => Input.AccountId(0));
        Assert.Throws<McpException>(() => Input.AccountId(-1));
    }

    [Theory]
    [InlineData(null, Direction.Incoming)]
    [InlineData("incoming", Direction.Incoming)]
    [InlineData("OUTGOING", Direction.Outgoing)]
    [InlineData(" all ", Direction.All)]
    public void Direction_is_parsed(string? value, Direction expected) =>
        Assert.Equal(expected, Input.ParseDirection(value));

    [Fact]
    public void Unknown_direction_is_refused() =>
        Assert.Throws<McpException>(() => Input.ParseDirection("tahsilat"));

    [Fact]
    public void Search_text_is_limited()
    {
        Assert.Null(Input.Search("  "));
        Assert.Equal("kuzey", Input.Search(" kuzey "));
        Assert.Throws<McpException>(() => Input.Search(new string('a', 101)));
    }
}
