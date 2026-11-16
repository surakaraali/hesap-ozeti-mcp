using System.Text.RegularExpressions;

namespace HesapOzeti.Mcp;

/// <summary>
/// Masks account and identity numbers before anything is handed to the model. Whatever leaves this server can end up
/// in a chat history, a model provider's logs or a screenshot, so the full numbers never leave it.
/// </summary>
public static partial class Masking
{
    private const string Hidden = "****";

    /// <summary>"TR09 0090 0010 ... 0001" becomes "TR09 **** 0001": the country, the check digits and the last four.</summary>
    public static string? Iban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
        {
            return null;
        }

        var compact = iban.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        // The shortest IBAN in use has 15 characters. Anything shorter is not an IBAN; show none of it.
        return compact.Length < 15 ? Hidden : $"{compact[..4]} {Hidden} {compact[^4..]}";
    }

    /// <summary>A tax number (VKN, 10 digits) or identity number (TCKN, 11 digits) keeps its last three digits.</summary>
    public static string? TaxNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var trimmed = number.Trim();
        return trimmed.Length < 8 ? Hidden : new string('*', trimmed.Length - 3) + trimmed[^3..];
    }

    /// <summary>
    /// The description of a bank movement is free text the sender typed. People write their IBAN, tax number or
    /// card number into it, so the text is searched for them as well, and cut to a length the model needs.
    /// </summary>
    public static string Text(string? text, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var masked = IbanInText().Replace(text, m => Iban(m.Value)!);
        masked = LongNumberInText().Replace(masked, m => TaxNumber(m.Value)!);
        return masked.Length <= maxLength ? masked : masked[..maxLength] + "…";
    }

    // Two letters, two check digits, then groups of four (with or without a space) and a shorter last group.
    // Whole groups only, so the match stops at the IBAN's end and does not swallow the next word or number.
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?\b")]
    private static partial Regex IbanInText();

    // 10 to 19 digits in a row: tax and identity numbers, card numbers. A bank reference that long is masked too, which is harmless.
    [GeneratedRegex(@"\b\d{10,19}\b")]
    private static partial Regex LongNumberInText();
}
