using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DerivedOutputDownloader3DX.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.Services;

/// <summary>
/// Authentification CAS sur 3DPassport (login ticket + POST login) avec cookies de session.
/// Les appels 3DSpace suivants réutilisent la même <see cref="HttpClient"/> (jarre à cookies partagée).
/// </summary>
/// <remarks>
/// Ne jamais journaliser le mot de passe, les cookies ni les jetons.
/// Les en-têtes Accept ne sont pas posés globalement : le GET ticket attend du JSON ;
/// le POST /login accepte HTML/XML (évite HTTP 406 si le serveur ne renvoie pas du JSON).
/// </remarks>
public sealed class ThreeDxCasPassportClient : IDisposable
{
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly ILogger<ThreeDxCasPassportClient> _log;

    public ThreeDxCasPassportClient(ILogger<ThreeDxCasPassportClient> log)
    {
        _log = log;
        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 DerivedOutputDownloader3DX/1.0");
    }

    public HttpClient Http => _http;

    /// <summary>Jarre à cookies partagée avec <see cref="Http"/>.</summary>
    public CookieContainer CookieContainer => _handler.CookieContainer;

    /// <summary>
    /// Obtient un login ticket puis envoie les identifiants CAS.
    /// </summary>
    public async Task LoginAsync(
        ThreeExperienceOptions opt,
        string passportBaseUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(passportBaseUrl))
        {
            throw new InvalidOperationException("PassportBaseUrl (ou THREE_DX_PASSPORT_URL) est requis pour le flux CAS.");
        }

        var baseUrl = passportBaseUrl.TrimEnd('/');
        var getUrl = $"{baseUrl}/login?action=get_auth_params";

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, getUrl);
        getRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _log.LogInformation(
            "[3DPassport] GET login ticket : {Url} | Accept envoyé : {Accept}",
            getUrl,
            FormatAcceptHeader(getRequest.Headers.Accept));

        using var getResp = await _http.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
        var getBody = await getResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!getResp.IsSuccessStatusCode)
        {
            _log.LogError(
                "[3DPassport] get_auth_params refusé. HTTP {Code} | URL : {Url} | Corps : {Body}",
                (int)getResp.StatusCode,
                getUrl,
                Truncate(getBody, 500));
            throw new InvalidOperationException(
                $"3DPassport get_auth_params HTTP {(int)getResp.StatusCode} : {Truncate(getBody, 300)}");
        }

        string? lt;
        try
        {
            using var doc = JsonDocument.Parse(getBody);
            if (!doc.RootElement.TryGetProperty("lt", out var ltEl))
            {
                throw new InvalidOperationException("Réponse get_auth_params sans propriété « lt ».");
            }
            lt = ltEl.GetString();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Réponse get_auth_params JSON illisible.", ex);
        }

        if (string.IsNullOrWhiteSpace(lt))
        {
            throw new InvalidOperationException("Login ticket « lt » vide.");
        }

        var service = string.IsNullOrWhiteSpace(opt.CasServiceUrl) ? null : opt.CasServiceUrl.Trim();
        var postPath = $"{baseUrl}/login";
        if (!string.IsNullOrEmpty(service))
        {
            postPath += "?service=" + Uri.EscapeDataString(service);
        }

        using var form = new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lt"] = lt,
                ["username"] = username,
                ["password"] = password,
                ["rememberMe"] = "no"
            });

        form.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
        {
            CharSet = "UTF-8"
        };

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, postPath);
        postRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        postRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        postRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        postRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        postRequest.Content = form;

        _log.LogInformation(
            "[3DPassport] POST login (utilisateur : {User}) | URL (sans secret) : {Url} | Accept : {Accept} | Content-Type : {CT}",
            username,
            postPath,
            FormatAcceptHeader(postRequest.Headers.Accept),
            postRequest.Content.Headers.ContentType?.ToString() ?? "(n/a)");

        using var postResp = await _http.SendAsync(postRequest, cancellationToken).ConfigureAwait(false);
        var postBody = await postResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!IsCasLoginHttpSuccess(postResp))
        {
            LogPostLoginFailure(postPath, postRequest, postResp, postBody, username);
            throw new InvalidOperationException(
                $"3DPassport login HTTP {(int)postResp.StatusCode} ({postResp.ReasonPhrase ?? ""}) : {Truncate(postBody, 400)}");
        }

        await SeedThreeDSpaceSessionCookiesAsync(opt, passportBaseUrl, cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "[3DPassport] Login terminé (HTTP {Code} {Reason}). Cookies conservés.",
            (int)postResp.StatusCode,
            postResp.ReasonPhrase ?? "");
    }

    /// <summary>
    /// Établit la session 3DSpace via un service ticket IAM dédié au domaine 3DSpace.
    /// Mécanisme : GET {PassportBaseUrl}/login?service={ThreeDSpaceUrl} avec le TGT cookie déjà
    /// présent dans le jar → IAM redirige vers 3DSpace avec un service ticket → 3DSpace valide et
    /// dépose ses cookies de session. Même principe que le x3ds_auth_url de FedSearch.
    /// </summary>
    private async Task SeedThreeDSpaceSessionCookiesAsync(
        ThreeExperienceOptions opt,
        string passportBaseUrl,
        CancellationToken cancellationToken)
    {
        var spaceUrl = opt.ThreeDSpaceUrl?.Trim();
        if (string.IsNullOrWhiteSpace(spaceUrl))
        {
            _log.LogWarning("[3DSpace] Graine cookies : ThreeDSpaceUrl vide — ignoré.");
            return;
        }

        var passportBase = passportBaseUrl.TrimEnd('/');

        // Étape 1 — service ticket IAM spécifique au domaine 3DSpace
        // Le TGT est déjà dans le jar de cookies (domaine IAM). IAM l'utilise pour émettre un
        // service ticket sans redemander les identifiants, puis redirige vers 3DSpace.
        var serviceTicketUrl =
            $"{passportBase}/login?service={Uri.EscapeDataString(spaceUrl.TrimEnd('/') + "/")}";

        _log.LogInformation("[3DSpace] Demande service ticket IAM → 3DSpace : {Url}", serviceTicketUrl);

        using var ticketReq = new HttpRequestMessage(HttpMethod.Get, serviceTicketUrl);
        ticketReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        ticketReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var ticketResp = await _http.SendAsync(ticketReq, cancellationToken).ConfigureAwait(false);
        _ = await ticketResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "[3DSpace] Service ticket IAM → HTTP {Code} {Reason} (après redirections auto).",
            (int)ticketResp.StatusCode,
            ticketResp.ReasonPhrase ?? "");

        if (ticketResp.IsSuccessStatusCode)
        {
            return;
        }

        // Étape 2 — fallback : GET direct sur la base 3DSpace
        _log.LogWarning("[3DSpace] Service ticket IAM non concluant — fallback GET direct : {Url}", spaceUrl);

        using var seedReq = new HttpRequestMessage(HttpMethod.Get, spaceUrl);
        seedReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        seedReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var seedResp = await _http.SendAsync(seedReq, cancellationToken).ConfigureAwait(false);
        _ = await seedResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!seedResp.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "[3DSpace] GET fallback HTTP {Code} {Reason} — poursuite.",
                (int)seedResp.StatusCode,
                seedResp.ReasonPhrase ?? "");
        }
        else
        {
            _log.LogInformation(
                "[3DSpace] GET fallback OK (HTTP {Code}).",
                (int)seedResp.StatusCode);
        }
    }

    /// <summary>
    /// 2xx : succès habituel. 301-308 : redirection (avec AllowAutoRedirect la réponse finale est 2xx).
    /// </summary>
    private static bool IsCasLoginHttpSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return true;
        var code = (int)response.StatusCode;
        return code is >= 300 and <= 399;
    }

    private void LogPostLoginFailure(
        string postPath,
        HttpRequestMessage postRequest,
        HttpResponseMessage postResp,
        string postBody,
        string username)
    {
        _log.LogError(
            "[3DPassport] POST login refusé. URL : {Url} | HTTP : {Code} | Utilisateur : {User} | Corps : {Body}",
            postPath,
            (int)postResp.StatusCode,
            username,
            Truncate(postBody, 500));

        if (postResp.StatusCode == HttpStatusCode.NotAcceptable)
        {
            _log.LogError(
                "[3DPassport] HTTP 406 Not Acceptable — Accept utilisé : {Accept}",
                FormatAcceptHeader(postRequest.Headers.Accept));
        }
    }

    private static string FormatAcceptHeader(HttpHeaderValueCollection<MediaTypeWithQualityHeaderValue> accept)
    {
        if (accept.Count == 0) return "(aucun)";
        return string.Join(", ", accept.Select(static a => a.MediaType));
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}
