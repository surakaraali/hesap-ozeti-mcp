namespace HesapOzeti.Mcp.Tests;

public class MaskingTests
{
    [Theory]
    [InlineData("TR340004605555000011112222", "TR34 **** 2222")]
    [InlineData("TR56 0006 4099 9900 0055 5566 66", "TR56 **** 6666")] // a bank that prints groups of four
    [InlineData("tr340004605555000011112222", "TR34 **** 2222")]
    [InlineData("TR12345", "****")] // too short to be an IBAN: nothing of it is shown
    public void Iban_keeps_only_country_check_digits_and_last_four(string iban, string expected) =>
        Assert.Equal(expected, Masking.Iban(iban));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Missing_values_stay_missing(string? value)
    {
        Assert.Null(Masking.Iban(value));
        Assert.Null(Masking.TaxNumber(value));
    }

    [Theory]
    [InlineData("1234567890", "*******890")] // VKN
    [InlineData("12345678901", "********901")] // TCKN
    [InlineData("12345", "****")]
    public void Tax_number_keeps_last_three_digits(string number, string expected) =>
        Assert.Equal(expected, Masking.TaxNumber(number));

    [Fact]
    public void Numbers_inside_free_text_are_masked()
    {
        var text = Masking.Text("FATURA ODEMESI VKN 1234567890 IBAN TR34 0004 6055 5500 0011 1122 22 KART 4543123412341234");

        Assert.DoesNotContain("1234567890", text);
        Assert.DoesNotContain("0004 6055", text);
        Assert.DoesNotContain("4543123412341234", text);
        Assert.Contains("FATURA ODEMESI VKN *******890 IBAN TR34 **** 2222", text);
    }

    [Fact]
    public void Ordinary_text_and_amounts_are_left_alone() =>
        Assert.Equal("FATURA 2026/118 TUTAR 18450.00", Masking.Text("FATURA 2026/118 TUTAR 18450.00"));

    [Fact]
    public void Long_text_is_cut() =>
        Assert.Equal(new string('A', 200) + "…", Masking.Text(new string('A', 500)));
}
