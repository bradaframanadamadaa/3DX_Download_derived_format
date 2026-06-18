using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.Services;

/// <summary>
/// Recherche 3DEXPERIENCE via le flux navigateur capturÃ© : <c>POST /federated/search</c>
/// avec une requÃªte titre ciblÃ©e sur <c>ds6w:label</c>.
/// </summary>
public sealed class FedSearchThreeDExperienceSearchService : IThreeDExperienceSearchService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ThreeExperienceOptions _opt;
    private readonly bool _disposeHttp;
    private readonly ILogger<FedSearchThreeDExperienceSearchService> _log;
    private bool _fedSearchSessionSeeded;

    public FedSearchThreeDExperienceSearchService(
        ThreeExperienceOptions opt,
        string bearerToken,
        ILogger<FedSearchThreeDExperienceSearchService> log)
        : this(opt, CreateDefaultHttpClient(), disposeHttp: true, bearerToken, log)
    {
    }

    public FedSearchThreeDExperienceSearchService(
        ThreeExperienceOptions opt,
        HttpClient http,
        bool disposeHttp,
        string? bearerToken,
        ILogger<FedSearchThreeDExperienceSearchService> log)
    {
        _opt = opt;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _disposeHttp = disposeHttp;
        _log = log;

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (!_http.DefaultRequestHeaders.Accept.Any())
        {
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        }
    }

    private static HttpClient CreateDefaultHttpClient() =>
        new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<ThreeDxSearchConnectivityResult> ProbeTitleSearchAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildSearchEndpoint();
        try
        {
            var candidates = await SearchByTitleAsync(
                    new ThreeDxSearchQuery(
                        PlatformUrl: _opt.PlatformUrl,
                        ThreeDSpaceUrl: _opt.ThreeDSpaceUrl,
                        SecurityContext: _opt.SecurityContext,
                        Title: title),
                    cancellationToken)
                .ConfigureAwait(false);

            return new ThreeDxSearchConnectivityResult(
                true,
                200,
                endpoint,
                $"FedSearch OK â€” {candidates.Count.ToString(CultureInfo.InvariantCulture)} candidat(s) mappÃ©(s) pour Â« {title} Â».");
        }
        catch (HttpRequestException ex)
        {
            return new ThreeDxSearchConnectivityResult(false, null, endpoint, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new ThreeDxSearchConnectivityResult(false, null, endpoint, ex.Message);
        }
    }

    public Task<IReadOnlyList<ThreeDxSearchCandidate>> SearchByTitleAsync(
        ThreeDxSearchQuery query,
        CancellationToken cancellationToken = default) =>
        ExecuteSearchAsync(query, byName: false, cancellationToken);

    public Task<IReadOnlyList<ThreeDxSearchCandidate>> SearchByNameAsync(
        ThreeDxSearchQuery query,
        CancellationToken cancellationToken = default) =>
        ExecuteSearchAsync(query, byName: true, cancellationToken);

    private async Task<IReadOnlyList<ThreeDxSearchCandidate>> ExecuteSearchAsync(
        ThreeDxSearchQuery query,
        bool byName,
        CancellationToken cancellationToken)
    {
        await EnsureFedSearchSessionAsync(cancellationToken).ConfigureAwait(false);

        var endpoint = BuildSearchEndpoint();
        var payload = BuildFedSearchPayload(query, byName);
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        _log.LogInformation(
            byName
                ? "[FedSearch] POST /federated/search — nom ciblé ds6w:identifier, tenant={Tenant}, top={Top}."
                : "[FedSearch] POST /federated/search — titre ciblé ds6w:label, tenant={Tenant}, top={Top}.",
            ResolveTenant(),
            Math.Clamp(_opt.SearchTop, 1, 200));

        var (statusCode, reason, body) = await SendFedSearchSearchAsync(endpoint, json, cancellationToken)
            .ConfigureAwait(false);
        if (statusCode is < 200 or > 299)
        {
            _log.LogWarning(
                "[FedSearch] Ã‰chec HTTP {Code} {Reason}. Endpoint : {Endpoint} | RÃ©ponse (extrait) : {Body}",
                statusCode,
                reason,
                endpoint,
                Truncate(body, 500));
            throw new InvalidOperationException(
                $"FedSearch HTTP {statusCode} {reason} : {Truncate(body, 300)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning("[FedSearch] RÃ©ponse sans tableau `results`.");
                return Array.Empty<ThreeDxSearchCandidate>();
            }

            var candidates = new List<ThreeDxSearchCandidate>();
            foreach (var result in results.EnumerateArray())
            {
                var candidate = MapResult(result);
                if (candidate != null)
                {
                    candidates.Add(candidate);
                }
            }

            return candidates;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "[FedSearch] JSON illisible.");
            return Array.Empty<ThreeDxSearchCandidate>();
        }
    }

    private async Task<(int StatusCode, string Reason, string Body)> SendFedSearchSearchAsync(
        string endpoint,
        string json,
        CancellationToken cancellationToken)
    {
        const int maxChallengeRounds = 3;
        var (statusCode, reason, body) = await SendFedSearchPostOnceAsync(endpoint, json, cancellationToken)
            .ConfigureAwait(false);

        for (var round = 0; round < maxChallengeRounds && statusCode is < 200 or > 299; round++)
        {
            var authUrl = TryExtractX3dsAuthUrl(body);
            if (string.IsNullOrWhiteSpace(authUrl))
            {
                break;
            }

            _log.LogInformation(
                "[FedSearch] Challenge CAS service ticket détecté (HTTP {Code}, tentative {Round}/{Max}). GET x3ds_auth_url puis retry.",
                statusCode,
                round + 1,
                maxChallengeRounds);

            await FollowServiceTicketChallengeAsync(authUrl, cancellationToken).ConfigureAwait(false);
            _fedSearchSessionSeeded = false;
            await EnsureFedSearchSessionAsync(cancellationToken).ConfigureAwait(false);

            (statusCode, reason, body) = await SendFedSearchPostOnceAsync(endpoint, json, cancellationToken)
                .ConfigureAwait(false);
        }

        return (statusCode, reason, body);
    }

    private async Task<(int StatusCode, string Reason, string Body)> SendFedSearchPostOnceAsync(
        string endpoint,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Accept-Language", "fr");
        AddBrowserLikeOriginHeaders(request);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, response.ReasonPhrase ?? "", body);
    }

    private async Task FollowServiceTicketChallengeAsync(string authUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, authUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.TryAddWithoutValidation("Accept-Language", "fr");
        AddBrowserLikeOriginHeaders(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _log.LogInformation(
            "[FedSearch] x3ds_auth_url suivi : HTTP {Code} {Reason} (corps non journalisÃ©).",
            (int)response.StatusCode,
            response.ReasonPhrase ?? "");
    }

    private static string? TryExtractX3dsAuthUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("x3ds_auth_url", out var authUrl) &&
                authUrl.ValueKind == JsonValueKind.String)
            {
                return authUrl.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private async Task EnsureFedSearchSessionAsync(CancellationToken cancellationToken)
    {
        if (_fedSearchSessionSeeded)
        {
            return;
        }

        _fedSearchSessionSeeded = true;
        var fedSearchUrl = ResolveFedSearchBaseUrl();
        var tenant = ResolveTenant();
        if (string.IsNullOrWhiteSpace(fedSearchUrl) || string.IsNullOrWhiteSpace(tenant))
        {
            return;
        }

        var loginUrl = $"{fedSearchUrl.TrimEnd('/')}/federated/login?tenant={Uri.EscapeDataString(tenant)}&xrequestedwith=xmlhttprequest";
        using var request = new HttpRequestMessage(HttpMethod.Get, loginUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddBrowserLikeOriginHeaders(request);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _log.LogInformation(
                "[FedSearch] Graine session /federated/login : HTTP {Code} {Reason}.",
                (int)response.StatusCode,
                response.ReasonPhrase ?? "");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[FedSearch] Graine session ignorÃ©e â€” poursuite vers /federated/search.");
        }
    }

    private object BuildFedSearchPayload(ThreeDxSearchQuery query, bool byName = false)
    {
        var name = query.Name?.Trim() ?? "";
        var title = query.Title?.Trim() ?? "";
        var top = Math.Clamp(_opt.SearchTop, 1, 200);
        var securityContext = NormalizeSecurityContext(query.SecurityContext ?? _opt.SecurityContext);
        var tenant = ResolveTenant();

        var fedQuery = byName
            ? "[ds6w:identifier]:\"" + EscapeFedSearchTerm(name) + "\""
            : "[ds6w:label]:*" + EscapeFedSearchTerm(title) + "*";

        return new Dictionary<string, object?>
        {
            ["specific_source_parameter"] = new Dictionary<string, object?>
            {
                ["3dspace"] = new Dictionary<string, object?>
                {
                    ["additional_query"] =
                        " AND NOT (flattenedtaxonomies:(\"types/Person\" OR \"types/Security Context\")) " +
                        "AND NOT (owner:\"ENOVIA_CLOUD\" OR owner:\"Service Creator\" OR owner:\"Corporate\" OR owner:\"User Agent\" OR owner:\"SLMInstallerAdmin\" OR owner:\"Creator\" OR owner:\"VPLMAdminUser\")",
                    ["option"] = new Dictionary<string, object?>
                    {
                        ["with_synthesis_ranged"] = true,
                        ["enable_mono_sixw"] = false
                    }
                },
                ["drive"] = new Dictionary<string, object?>
                {
                    ["additional_query"] =
                        " AND NOT ([flattenedtaxonomies]:\"types/DriveNode\" AND ( [current]:\"Trashed\" OR [policy]:\"Drive File Iteration\") )"
                }
            },
            ["with_indexing_date"] = true,
            ["with_synthesis"] = true,
            ["with_nls"] = false,
            ["label"] = "DerivedOutputDownloader3DX-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["locale"] = "fr",
            ["select_predicate"] = new[]
            {
                "ds6w:label",
                "ds6w:type",
                "ds6w:description",
                "ds6w:identifier",
                "ds6w:modified",
                "ds6w:created",
                "ds6wg:revision",
                "ds6w:status",
                "ds6w:responsible",
                "owner",
                "ds6w:responsibleUid",
                "ds6w:project",
                "ds6w:dataSource",
                "ds6w:community",
                "ds6w:originator",
                "ds6w:repository",
                "dcterms:title",
                "dcterms:description",
                "ds6w:containerUid"
            },
            ["with_synthesis_hierarchical"] = true,
            ["select_file"] = new[] { "icon", "thumbnail_2d" },
            ["query"] = fedQuery,
            ["refine"] = new Dictionary<string, object?>(),
            ["select_exclude_synthesis"] = new[] { "ds6w:what/ds6w:topic" },
            ["order_by"] = "desc",
            ["order_field"] = "relevance",
            ["select_snippets"] = new[]
            {
                "ds6w:snippet",
                "ds6w:label:snippet",
                "ds6w:responsible:snippet",
                "ds6w:community:snippet",
                "swym:message_text:snippet"
            },
            ["nresults"] = top,
            ["start"] = "0",
            ["source"] = new[] { "swym", "3dspace", "drive", "usersgroup", "3dplan", "dashboard", "3dmessaging" },
            ["tenant"] = tenant,
            ["login"] = new Dictionary<string, object?>
            {
                ["3dspace"] = new Dictionary<string, object?>
                {
                    ["SecurityContext"] = securityContext
                }
            }
        };
    }

    private ThreeDxSearchCandidate? MapResult(JsonElement result)
    {
        if (!result.TryGetProperty("attributes", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var title = PickAttribute(attributes, "ds6w:label", "dcterms:title");
        var id = PickAttribute(attributes, "resourceid", "ds6w:identifier");
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new ThreeDxSearchCandidate
        {
            Id = id ?? "",
            Type = PickAttribute(attributes, "ds6w:what/ds6w:type", "ds6w:type") ?? "",
            Title = title ?? "",
            Name = PickAttribute(attributes, "ds6w:identifier") ?? "",
            Revision = PickAttribute(attributes, "ds6wg:revision") ?? "",
            MaturityState = PickAttribute(attributes, "ds6w:what/ds6w:status", "ds6w:status") ?? "",
            Owner = PickAttribute(attributes, "owner", "ds6w:who/ds6w:responsible", "ds6w:responsible") ?? "",
            CollaborativeSpace = PickAttribute(attributes, "ds6w:where/ds6w:context/ds6w:project", "ds6w:project") ?? "",
            Url = null,
            ConfidenceScore = 0
        };
    }

    private static string? PickAttribute(JsonElement attributes, params string[] names)
    {
        foreach (var expected in names)
        {
            foreach (var attr in attributes.EnumerateArray())
            {
                if (!attr.TryGetProperty("name", out var nameProp) ||
                    !string.Equals(nameProp.GetString(), expected, StringComparison.OrdinalIgnoreCase) ||
                    !attr.TryGetProperty("value", out var valueProp))
                {
                    continue;
                }

                if (valueProp.ValueKind == JsonValueKind.String)
                {
                    return valueProp.GetString();
                }

                if (valueProp.ValueKind == JsonValueKind.Number ||
                    valueProp.ValueKind == JsonValueKind.True ||
                    valueProp.ValueKind == JsonValueKind.False)
                {
                    return valueProp.GetRawText();
                }
            }
        }

        return null;
    }

    private string BuildSearchEndpoint() =>
        $"{ResolveFedSearchBaseUrl().TrimEnd('/')}/federated/search?xrequestedwith=xmlhttprequest";

    private string ResolveFedSearchBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_opt.FedSearchUrl))
        {
            return _opt.FedSearchUrl.Trim();
        }

        var sourceUrl = !string.IsNullOrWhiteSpace(_opt.ThreeDSpaceUrl)
            ? _opt.ThreeDSpaceUrl
            : _opt.PlatformUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new InvalidOperationException("FedSearchUrl absent et impossible Ã  dÃ©river : ThreeDSpaceUrl / PlatformUrl vides.");
        }

        var uri = new Uri(sourceUrl.Trim());
        var host = uri.Host
            .Replace("-space.", "-fedsearch.", StringComparison.OrdinalIgnoreCase)
            .Replace("-ifwe.", "-fedsearch.", StringComparison.OrdinalIgnoreCase);
        return $"{uri.Scheme}://{host}";
    }

    private string ResolveTenant()
    {
        if (!string.IsNullOrWhiteSpace(_opt.Tenant))
        {
            return _opt.Tenant.Trim().ToUpperInvariant();
        }

        foreach (var url in new[] { _opt.ThreeDSpaceUrl, _opt.FedSearchUrl, _opt.PlatformUrl })
        {
            var tenant = TryDeriveTenant(url);
            if (!string.IsNullOrWhiteSpace(tenant))
            {
                return tenant;
            }
        }

        throw new InvalidOperationException("Tenant 3DEXPERIENCE introuvable. Renseignez ThreeExperience:Tenant.");
    }

    private static string? TryDeriveTenant(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            var host = new Uri(url.Trim()).Host;
            var idx = host.IndexOf("-eu", StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? host[..idx].ToUpperInvariant() : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private void AddBrowserLikeOriginHeaders(HttpRequestMessage request)
    {
        var origin = string.IsNullOrWhiteSpace(_opt.PlatformUrl) ? "" : _opt.PlatformUrl.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(origin))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Referer", origin + "/");
    }

    private static string NormalizeSecurityContext(string? securityContext)
    {
        var sc = securityContext?.Trim() ?? "";
        return sc.StartsWith("ctx::", StringComparison.OrdinalIgnoreCase) ? sc[5..] : sc;
    }

    private static string EscapeFedSearchTerm(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "â€¦";

    public void Dispose()
    {
        if (_disposeHttp)
        {
            _http.Dispose();
        }
    }
}

