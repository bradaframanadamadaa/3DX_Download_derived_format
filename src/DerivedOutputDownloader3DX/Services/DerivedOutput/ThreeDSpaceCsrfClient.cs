using System.Text.Json;
using DerivedOutputDownloader3DX.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.Services.DerivedOutput;

/// <summary>
/// Récupère le jeton CSRF 3DSpace requis avant POST/PUT/DELETE (Postman primer DS).
/// Prêt pour l'implémentation dsdo — non utilisé tant que les endpoints derived outputs ne sont pas validés.
/// </summary>
public sealed class ThreeDSpaceCsrfClient
{
    private readonly HttpClient _http;
    private readonly ThreeExperienceOptions _options;
    private readonly ILogger<ThreeDSpaceCsrfClient> _log;

    public ThreeDSpaceCsrfClient(
        HttpClient http,
        ThreeExperienceOptions options,
        ILogger<ThreeDSpaceCsrfClient> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public async Task<string?> FetchCsrfTokenAsync(CancellationToken cancellationToken = default)
    {
        var url = DerivedOutputApiEndpoints.CsrfUrl(_options.ThreeDSpaceUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplySecurityContext(request);

        _log.LogInformation("[3DSpace] GET CSRF : {Url}", url);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "[3DSpace] CSRF refusé HTTP {Code}. Corps (extrait) : {Body}",
                (int)response.StatusCode,
                Truncate(body, 300));
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("csrf", out var csrf) &&
                csrf.TryGetProperty("value", out var value))
            {
                return value.GetString();
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "[3DSpace] Réponse CSRF illisible.");
        }

        return null;
    }

    private void ApplySecurityContext(HttpRequestMessage request)
    {
        var sc = _options.SecurityContext?.Trim();
        if (!string.IsNullOrWhiteSpace(sc))
        {
            request.Headers.TryAddWithoutValidation("SecurityContext", sc);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
