using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using DerivedOutputDownloader3DX.Services.DerivedOutput;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX;

/// <summary>
/// Client REST Derived Outputs (famille dsdo) — authentification CAS (cookies).
/// Flux complet :
///   1. Auto-découverte du SecurityContext via pno/person si le placeholder est détecté.
///   2. POST Locate pour lister les fichiers dérivés d'un objet.
///   3. POST DownloadTicket (avec CSRF) pour obtenir le ticket FCS.
///   4. GET FCS (SDK) puis fallback POST form-urlencoded pour télécharger le binaire.
/// </summary>
public sealed class DerivedOutputClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ThreeExperienceOptions _opts;
    private readonly ILogger<DerivedOutputClient> _log;

    // SecurityContext résolu une fois par instance (lazy, thread-safe via SemaphoreSlim)
    private string? _resolvedSc;
    private readonly SemaphoreSlim _scLock = new(1, 1);

    public DerivedOutputClient(
        HttpClient http,
        ThreeExperienceOptions options,
        ILogger<DerivedOutputClient> log)
    {
        _http = http;
        _opts = options;
        _log = log;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API publique
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Liste les derived outputs disponibles pour un objet (physicalId).
    /// Essaie d'abord le corpus <c>dsdrw</c> (Drawing), puis <c>dseng</c> (Part/Assembly).
    /// </summary>
    public async Task<IReadOnlyList<DerivedOutputDescriptor>> ListDerivedOutputsAsync(
        string physicalId,
        CancellationToken ct = default)
    {
        var sc = await EnsureSecurityContextAsync(ct).ConfigureAwait(false);

        // Tente Drawing en premier (cas le plus probable pour l'utilisateur actuel)
        var results = await LocateAsync(physicalId, "dsdrw", "dsdrw:Drawing", sc, ct).ConfigureAwait(false);
        if (results.Count > 0)
        {
            _log.LogInformation("[dsdo] Corpus dsdrw:Drawing → {Count} fichier(s) trouvé(s).", results.Count);
            return results;
        }

        _log.LogInformation("[dsdo] Corpus dsdrw vide — essai dseng:EngItem.");
        results = await LocateAsync(physicalId, "dseng", "dseng:EngItem", sc, ct).ConfigureAwait(false);
        _log.LogInformation("[dsdo] Corpus dseng:EngItem → {Count} fichier(s) trouvé(s).", results.Count);
        return results;
    }

    /// <summary>
    /// Télécharge un fichier dérivé via le flux DownloadTicket → FCS.
    /// Retourne le chemin absolu du fichier écrit sur disque.
    /// </summary>
    public async Task<string> DownloadDerivedOutputAsync(
        DerivedOutputDescriptor descriptor,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var sc = await EnsureSecurityContextAsync(ct).ConfigureAwait(false);

        // Étape 1 — CSRF
        var csrf = await FetchCsrfAsync(sc, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(csrf))
        {
            throw new InvalidOperationException(
                "Impossible d'obtenir le token CSRF 3DSpace. Vérifiez l'authentification et le SecurityContext.");
        }

        // Étape 2 — DownloadTicket
        var (ticketUrl, ticket, serverFileName) = await GetDownloadTicketAsync(
            descriptor.ParentId, descriptor.Id, sc, csrf, ct).ConfigureAwait(false);

        // Étape 3 — FCS
        var fileName = serverFileName ?? descriptor.FileName;
        return await DownloadViaFcsAsync(ticketUrl, ticket, fileName, outputDirectory, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SecurityContext
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Résout le SecurityContext : utilise la config si non-placeholder, sinon appelle pno/person.
    /// </summary>
    public async Task<string> EnsureSecurityContextAsync(CancellationToken ct = default)
    {
        if (_resolvedSc != null) return _resolvedSc;

        await _scLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_resolvedSc != null) return _resolvedSc;

            var fromConfig = NormalizeSc(_opts.SecurityContext);
            if (!IsPlaceholder(fromConfig))
            {
                _resolvedSc = await ResolveConfiguredSecurityContextAsync(fromConfig, ct).ConfigureAwait(false);
                _log.LogInformation("[dsdo] SecurityContext (config) : {Sc}", _resolvedSc);
                return _resolvedSc;
            }

            _log.LogInformation(
                "[dsdo] SecurityContext absent / placeholder → auto-découverte via pno/person.");
            _resolvedSc = await DiscoverSecurityContextAsync(ct).ConfigureAwait(false);
            _log.LogInformation("[dsdo] SecurityContext (découvert) : {Sc}", _resolvedSc);
            return _resolvedSc;
        }
        finally
        {
            _scLock.Release();
        }
    }

    /// <summary>
    /// Retourne tous les SecurityContexts disponibles pour l'utilisateur courant.
    /// Utile pour la commande --list-contexts.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailableSecurityContextsAsync(CancellationToken ct = default)
    {
        var contexts = await FetchCollabspacesAsync(ct).ConfigureAwait(false);
        return contexts;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Locate (POST)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<DerivedOutputDescriptor>> LocateAsync(
        string physicalId,
        string corpus,
        string objectType,
        string securityContext,
        CancellationToken ct)
    {
        var tenant = _opts.Tenant?.Trim() ?? "";
        var url = DerivedOutputApiEndpoints.LocateUrl(_opts.ThreeDSpaceUrl, tenant);

        // relativePath : sans slash initial, format attendu par dsdo
        var relativePath = $"resource/v1/{corpus}/{objectType}/{physicalId}";

        var payload = new
        {
            referencedObject = new[]
            {
                new
                {
                    source = "3DSpace",
                    type = "VPMReference",
                    id = physicalId,
                    relativePath
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        _log.LogInformation("[dsdo] POST Locate ({Corpus}) : {Url}", corpus, url);
        _log.LogDebug("[dsdo] Corps Locate : {Json}", json);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplySc(request, securityContext);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _log.LogInformation(
            "[dsdo] Locate HTTP {Code} — {Len} car. | {Preview}",
            (int)response.StatusCode,
            body.Length,
            Truncate(body, 600));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"dsdo Locate HTTP {(int)response.StatusCode} {response.ReasonPhrase} : {Truncate(body, 400)}");
        }

        return ParseLocateResponse(body);
    }

    private IReadOnlyList<DerivedOutputDescriptor> ParseLocateResponse(string body)
    {
        var results = new List<DerivedOutputDescriptor>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // La réponse est soit un tableau direct, soit { "member": [...] }
            JsonElement memberArray;
            if (root.ValueKind == JsonValueKind.Array)
                memberArray = root;
            else if (!root.TryGetProperty("member", out memberArray) || memberArray.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning("[dsdo] Locate : structure JSON inattendue, ni tableau ni \"member\".");
                return results;
            }

            foreach (var entry in memberArray.EnumerateArray())
            {
                // Chaque entrée peut avoir un wrapper "derivedOutputs" ou directement les données
                var root2 = entry.TryGetProperty("derivedOutputs", out var wrapped) ? wrapped : entry;

                var parentId = GetStr(root2, "id", "physicalid", "pid") ?? "";

                // Les fichiers sont dans "derivedOutputfiles" ou "files"
                JsonElement filesArray;
                if (!root2.TryGetProperty("derivedOutputfiles", out filesArray) &&
                    !root2.TryGetProperty("files", out filesArray))
                {
                    // Pas de sous-tableau : essayer si root2 lui-même est un fichier
                    var singleDesc = MapFileDescriptor(root2, parentId);
                    if (singleDesc != null) results.Add(singleDesc);
                    continue;
                }

                if (filesArray.ValueKind != JsonValueKind.Array) continue;

                foreach (var fileItem in filesArray.EnumerateArray())
                {
                    var desc = MapFileDescriptor(fileItem, parentId);
                    if (desc != null) results.Add(desc);
                }
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "[dsdo] Impossible de parser la réponse Locate.");
        }

        return results;
    }

    private static DerivedOutputDescriptor? MapFileDescriptor(JsonElement item, string parentId)
    {
        var id = GetStr(item, "id", "physicalid", "pid");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var format = GetStr(item, "format", "dsdo:format", "type")
            ?? DetectFormatFromFileName(GetStr(item, "filename", "name", "fileName") ?? "");
        var fileName = GetStr(item, "filename", "name", "fileName") ?? $"{id}.bin";
        var isExchange = GetBool(item, "dsdo:isExchangeFormat", "isExchangeFormat") ?? true;
        var downloadUrl = GetStr(item, "dsdo:downloadTicket", "downloadUrl", "href");

        return new DerivedOutputDescriptor
        {
            Id = id,
            ParentId = parentId,
            Format = format ?? "",
            FileName = fileName,
            IsExchangeFormat = isExchange,
            DownloadUrl = downloadUrl
        };
    }

    private static string? DetectFormatFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        return ext.Length > 0 ? ext : null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CSRF
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string?> FetchCsrfAsync(string securityContext, CancellationToken ct)
    {
        var tenant = _opts.Tenant?.Trim() ?? "";
        var url = DerivedOutputApiEndpoints.CsrfUrl(_opts.ThreeDSpaceUrl, tenant);

        _log.LogInformation("[3DSpace] GET CSRF : {Url}", url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplySc(request, securityContext);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("[3DSpace] CSRF HTTP {Code} : {Body}", (int)response.StatusCode, Truncate(body, 300));
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("csrf", out var csrf) &&
                csrf.TryGetProperty("value", out var val))
            {
                return val.GetString();
            }
        }
        catch (JsonException) { /* ignoré */ }

        _log.LogWarning("[3DSpace] Token CSRF introuvable dans la réponse : {Body}", Truncate(body, 200));
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DownloadTicket (POST)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<(string ticketUrl, string ticket, string? fileName)> GetDownloadTicketAsync(
        string parentId,
        string fileId,
        string securityContext,
        string csrfToken,
        CancellationToken ct)
    {
        var tenant = _opts.Tenant?.Trim() ?? "";
        var url = DerivedOutputApiEndpoints.DownloadTicketUrl(_opts.ThreeDSpaceUrl, parentId, fileId, tenant);

        _log.LogInformation("[dsdo] POST DownloadTicket : {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplySc(request, securityContext);
        request.Headers.TryAddWithoutValidation("ENO_CSRF_TOKEN", csrfToken);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _log.LogInformation(
            "[dsdo] DownloadTicket HTTP {Code} — {Preview}",
            (int)response.StatusCode,
            Truncate(body, 400));

        if (!response.IsSuccessStatusCode)
        {
            var hint = body.Contains("Export rights", StringComparison.OrdinalIgnoreCase)
                ? " Droits Export requis : vérifiez Platform Manager → Data Exchange, ou testez le téléchargement depuis l'UI 3DEXPERIENCE avec le même compte."
                : "";
            throw new InvalidOperationException(
                $"dsdo DownloadTicket HTTP {(int)response.StatusCode} {response.ReasonPhrase} : {Truncate(body, 400)}{hint}");
        }

        return ParseTicketResponse(body);
    }

    private (string ticketUrl, string ticket, string? fileName) ParseTicketResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var payload = UnwrapDownloadTicketPayload(doc.RootElement);

            var ticketUrl = GetStr(payload, "ticketURL", "ticketUrl") ?? "";
            var ticket    = GetStr(payload, "ticket") ?? "";
            var fileName  = GetStr(payload, "filename", "fileName", "name");

            var (fcsUrl, jobTicket) = NormalizeFcsTicket(ticketUrl, ticket);
            if (string.IsNullOrWhiteSpace(fcsUrl) || string.IsNullOrWhiteSpace(jobTicket))
            {
                throw new InvalidOperationException(
                    $"DownloadTicket : ticketURL ou ticket manquant. Réponse : {Truncate(body, 300)}");
            }

            return (fcsUrl, jobTicket, fileName);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"DownloadTicket : réponse JSON illisible. Détail : {ex.Message}. Corps : {Truncate(body, 300)}");
        }
    }

    /// <summary>
    /// Déplie la réponse SDK : <c>{ data: { dataelements: { ticketURL, ticket, filename } } }</c>.
    /// </summary>
    private static JsonElement UnwrapDownloadTicketPayload(JsonElement root)
    {
        if (root.TryGetProperty("dataelements", out var dataElements))
            return dataElements;

        if (root.TryGetProperty("data", out var data))
            return UnwrapDownloadTicketPayload(data);

        return root;
    }

    /// <summary>
    /// Certaines réponses inversent ticket / ticketURL ; on détecte l'URL FCS via le préfixe http(s).
    /// </summary>
    private static (string fcsUrl, string jobTicket) NormalizeFcsTicket(string ticketUrl, string ticket)
    {
        var a = ticketUrl.Trim();
        var b = ticket.Trim();
        var aIsUrl = LooksLikeHttpUrl(a);
        var bIsUrl = LooksLikeHttpUrl(b);

        if (aIsUrl && !bIsUrl) return (a, b);
        if (bIsUrl && !aIsUrl) return (b, a);
        return (a, b);
    }

    private static bool LooksLikeHttpUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // FCS download — GET (SDK) puis fallback POST form-urlencoded
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> DownloadViaFcsAsync(
        string ticketUrl,
        string ticket,
        string fileName,
        string outputDirectory,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);

        var getUrl = BuildFcsGetUrl(ticketUrl, ticket);
        _log.LogInformation("[FCS] GET téléchargement (SDK) : {Url}", Truncate(getUrl, 200));

        try
        {
            return await SaveFcsResponseAsync(getUrl, HttpMethod.Get, content: null, fileName, outputDirectory, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsRetriableFcsError(ex))
        {
            _log.LogWarning("[FCS] GET échoué — essai POST form-urlencoded : {Detail}", ex.Message);
        }

        _log.LogInformation("[FCS] POST téléchargement : {Url}", ticketUrl);

        var formBody = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__fcs__jobTicket", ticket)
        });

        return await SaveFcsResponseAsync(ticketUrl, HttpMethod.Post, formBody, fileName, outputDirectory, ct)
            .ConfigureAwait(false);
    }

    private static string BuildFcsGetUrl(string fcsUrl, string jobTicket)
    {
        var separator = fcsUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{fcsUrl}{separator}__fcs__jobTicket={Uri.EscapeDataString(jobTicket)}";
    }

    private static bool IsRetriableFcsError(InvalidOperationException ex)
    {
        var msg = ex.Message;
        return msg.Contains("HTTP 405", StringComparison.Ordinal) ||
               msg.Contains("HTTP 404", StringComparison.Ordinal) ||
               msg.Contains("HTTP 400", StringComparison.Ordinal) ||
               msg.Contains("HTTP 403", StringComparison.Ordinal);
    }

    private async Task<string> SaveFcsResponseAsync(
        string url,
        HttpMethod method,
        HttpContent? content,
        string fileName,
        string outputDirectory,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        _log.LogInformation(
            "[FCS] {Method} HTTP {Code} | Content-Type : {CT} | Content-Length : {Len}",
            method,
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? "(n/a)",
            response.Content.Headers.ContentLength?.ToString() ?? "(n/a)");

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"FCS download {method} HTTP {(int)response.StatusCode} {response.ReasonPhrase} : {Truncate(errBody, 400)}");
        }

        var resolvedName = ResolveFileName(response, fileName);
        var destPath = Path.Combine(outputDirectory, resolvedName);

        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fileStream, ct).ConfigureAwait(false);

        _log.LogInformation("[FCS] Fichier écrit : {Path} ({Size} o)", destPath, fileStream.Length);
        return destPath;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pno/person — découverte SecurityContext
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> ResolveConfiguredSecurityContextAsync(string fromConfig, CancellationToken ct)
    {
        IReadOnlyList<string> available;
        try
        {
            available = await FetchCollabspacesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[dsdo] Impossible de valider le SecurityContext config — utilisation directe.");
            return fromConfig;
        }

        if (available.Count == 0)
        {
            return fromConfig;
        }

        // Correspondance exacte
        var exact = available.FirstOrDefault(c => c.Equals(fromConfig, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        // Même espace collaboratif (dernier segment), rôle potentiellement différent dans la config
        var configCollab = fromConfig.Split('.')[^1];
        var byCollab = available.FirstOrDefault(c =>
            c.EndsWith("." + configCollab, StringComparison.OrdinalIgnoreCase));
        if (byCollab != null)
        {
            _log.LogWarning(
                "[dsdo] SecurityContext config « {Config} » introuvable — utilisation de « {Resolved} » (espace {Collab}).",
                fromConfig,
                byCollab,
                configCollab);
            return byCollab;
        }

        _log.LogWarning(
            "[dsdo] SecurityContext config « {Config} » introuvable parmi {Count} contexte(s) — premier disponible : {Fallback}.",
            fromConfig,
            available.Count,
            available[0]);
        return available[0];
    }

    private async Task<string> DiscoverSecurityContextAsync(CancellationToken ct)
    {
        var contexts = await FetchCollabspacesAsync(ct).ConfigureAwait(false);
        if (contexts.Count == 0)
        {
            throw new InvalidOperationException(
                "Aucun espace collaboratif trouvé pour cet utilisateur. " +
                "Vérifiez que le compte est correctement configuré dans 3DEXPERIENCE.");
        }

        // Premier contexte par défaut (l'utilisateur peut le préciser via appsettings.json)
        return contexts[0];
    }

    private async Task<IReadOnlyList<string>> FetchCollabspacesAsync(CancellationToken ct)
    {
        var tenant = _opts.Tenant?.Trim() ?? "";
        var url = DerivedOutputApiEndpoints.PnoPersonUrl(_opts.ThreeDSpaceUrl, tenant);

        _log.LogInformation("[pno] GET person collabspaces : {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _log.LogInformation(
            "[pno] HTTP {Code} — {Preview}", (int)response.StatusCode, Truncate(body, 500));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"pno/person HTTP {(int)response.StatusCode} {response.ReasonPhrase} : {Truncate(body, 400)}");
        }

        return ParseCollabspaces(body);
    }

    private IReadOnlyList<string> ParseCollabspaces(string body)
    {
        var results = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Cherche "collabspaces" à la racine ou dans la première propriété si tableau
            JsonElement csArray;
            if (!root.TryGetProperty("collabspaces", out csArray) || csArray.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning("[pno] Clé \"collabspaces\" introuvable. Brut : {Body}", Truncate(body, 400));
                return results;
            }

            foreach (var cs in csArray.EnumerateArray())
            {
                var csName = GetStr(cs, "name", "title") ?? "";
                if (!cs.TryGetProperty("couples", out var couples) || couples.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var couple in couples.EnumerateArray())
                {
                    if (!couple.TryGetProperty("role", out var roleEl) ||
                        !couple.TryGetProperty("organization", out var orgEl))
                        continue;

                    var role = GetStr(roleEl, "name") ?? "";
                    var org  = GetStr(orgEl,  "name") ?? "";
                    if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(org))
                    {
                        results.Add($"{role}.{org}.{csName}");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "[pno] Réponse illisible.");
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplySc(HttpRequestMessage request, string securityContext)
    {
        if (!string.IsNullOrWhiteSpace(securityContext))
        {
            // URL-encode pour les espaces dans l'org name (ex. "Company Name")
            request.Headers.TryAddWithoutValidation(
                "SecurityContext",
                Uri.EscapeDataString(securityContext));
        }
    }

    private static string NormalizeSc(string? sc)
    {
        var s = sc?.Trim() ?? "";
        return s.StartsWith("ctx::", StringComparison.OrdinalIgnoreCase) ? s[5..] : s;
    }

    private static bool IsPlaceholder(string sc) =>
        string.IsNullOrWhiteSpace(sc) ||
        sc.Equals("A_COMPLETER", StringComparison.OrdinalIgnoreCase) ||
        sc.Contains("VOTRE_", StringComparison.OrdinalIgnoreCase);

    private static string ResolveFileName(HttpResponseMessage response, string fallback)
    {
        var cd = response.Content.Headers.ContentDisposition;
        if (cd != null)
        {
            var fn = cd.FileNameStar ?? cd.FileName;
            if (!string.IsNullOrWhiteSpace(fn)) return Sanitize(fn.Trim('"'));
        }

        return string.IsNullOrWhiteSpace(fallback) ? "derived_output.bin" : Sanitize(fallback);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static string? GetStr(JsonElement el, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    private static bool? GetBool(JsonElement el, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (el.TryGetProperty(key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True)  return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
            }
        }
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
