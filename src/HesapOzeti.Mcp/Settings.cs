namespace HesapOzeti.Mcp;

/// <summary>
/// The two environment variables the server needs. The token is read from the environment only:
/// not from a command line argument (visible in the process list) and not from a file in the repository.
/// </summary>
public sealed record Settings(Uri? ApiUrl, string? Token, string? Error)
{
    public const string ApiUrlVariable = "HESAP_OZETI_API_URL";
    public const string TokenVariable = "HESAP_OZETI_TOKEN";

    public static Settings FromEnvironment(Func<string, string?> read)
    {
        var token = read(TokenVariable)?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return new(null, null, $"{TokenVariable} is not set. Put the API's JWT (a viewer token is enough) into this environment variable.");
        }

        var url = read(ApiUrlVariable)?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            url = "http://localhost:8080/";
        }

        if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var apiUrl)
            || apiUrl.Scheme is not ("http" or "https"))
        {
            return new(null, null, $"{ApiUrlVariable} must be an http or https URL.");
        }

        // The token travels in a header. Over plain http it is readable on the network, so http is only for this machine.
        if (apiUrl.Scheme == "http" && !apiUrl.IsLoopback)
        {
            return new(null, null, $"{ApiUrlVariable} must use https unless the API runs on this machine.");
        }

        return new(apiUrl, token, null);
    }
}
