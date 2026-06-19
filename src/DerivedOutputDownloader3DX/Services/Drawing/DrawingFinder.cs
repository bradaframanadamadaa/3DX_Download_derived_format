using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Services.DerivedOutput;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.Services.Drawing;

/// <summary>
/// Trouve les mises en plan (dsxcad:Drawing) liées à une pièce (VPMReference).
///
/// Approche officielle (source : SDK open-source Dassault Systèmes, repo 3ds-cpe-emed/3dxws-dotnet-core-sdk) :
///
///   POST /resources/v1/modeler/dsxcad/dsxcad:Drawing/Locate
///   Body : { "referencedObject": [{ "id": "{partPhysicalId}", "source": "3DSpace",
///                                    "type": "VPMReference", "relativePath": "/" }] }
///
/// Cette API retrouve tous les Drawings (mises en plan CATIA) qui référencent un Physical Product.
/// Le mécanisme est symétrique à dsdo:DerivedOutputs/Locate déjà utilisé pour les formats dérivés.
///
/// Cf. xCADDrawingService.Locate dans le SDK DS :
///   https://github.com/3ds-cpe-emed/3dxws-dotnet-core-sdk/blob/master/ds.enovia.dsxcad/service/xCADDrawingService.cs
/// </summary>
public sealed class DrawingFinder
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ThreeExperienceOptions _opts;
    private readonly ILogger<DrawingFinder> _log;

    public DrawingFinder(
        HttpClient http,
        ThreeExperienceOptions opts,
        ILogger<DrawingFinder> log)
    {
        _http = http;
        _opts = opts;
        _log  = log;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API publique
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retourne les mises en plan (dsxcad:Drawing) liées à une pièce (VPMReference).
    /// </summary>
    public async Task<IReadOnlyList<DrawingRef>> FindDrawingsForPartAsync(
        string partPhysicalId,
        string securityContext,
        CancellationToken ct = default)
    {
        var sc     = NormalizeSc(securityContext);
        var tenant = _opts.Tenant?.Trim() ?? "";

        // CSRF requis pour les POST sur 3DSpace
        var csrf = await FetchCsrfAsync(sc, ct).ConfigureAwait(false);

        var url = $"{Base()}/resources/v1/modeler/dsxcad/dsxcad:Drawing/Locate" +
                  $"?tenant={Uri.EscapeDataString(tenant)}";

        _log.LogInformation("[Drawing] Locate URL : {Url}", url);

        // Corps de la requête.
        // Clé : relativePath doit pointer sur le chemin dsxcad:Part de la pièce,
        // pas sur "/" ni sur dseng:EngItem.
        // Découvert empiriquement via inspection réseau sur l'interface 3DEXPERIENCE.
        var relativePath = $"resource/v1/dsxcad/dsxcad:Part/{partPhysicalId}";
        var payload = new
        {
            referencedObject = new[]
            {
                new
                {
                    source       = "3DSpace",
                    type         = "VPMReference",
                    id           = partPhysicalId,
                    relativePath
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        _log.LogInformation("[Drawing] Corps Locate : {Json}", json);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("SecurityContext", sc);
            if (!string.IsNullOrWhiteSpace(csrf))
                req.Headers.TryAddWithoutValidation("ENO_CSRF_TOKEN", csrf);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            _log.LogInformation(
                "[Drawing] Locate HTTP {Code} — réponse brute :\n{Body}",
                (int)resp.StatusCode, Truncate(body, 3000));

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "[Drawing] Locate HTTP {Code} pour pièce {Id} — pas de mises en plan.",
                    (int)resp.StatusCode, partPhysicalId);
                return Array.Empty<DrawingRef>();
            }

            var drawings = ParseLocateResponse(body);
            _log.LogInformation(
                "[Drawing] Pièce {Id} → {Count} mise(s) en plan trouvée(s).",
                partPhysicalId, drawings.Count);
            return drawings;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Drawing] Locate — exception pour pièce {Id}.", partPhysicalId);
            return Array.Empty<DrawingRef>();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Parsing de la réponse
    // ─────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<DrawingRef> ParseLocateResponse(string json)
    {
        var results = new List<DrawingRef>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // La réponse peut être un tableau direct ou { "member": [...] }
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else if (root.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.Array)
                arr = m;
            else
            {
                _log.LogInformation(
                    "[Drawing] Structure de réponse inconnue (root={K}). Contenu : {Body}",
                    root.ValueKind, Truncate(json, 500));
                return results;
            }

            foreach (var item in arr.EnumerateArray())
            {
                // Essayer de trouver un sous-objet "drawing" ou lire directement
                var target = item.TryGetProperty("drawing", out var nested) ? nested : item;

                var physId = GetStr(target, "physicalid")
                          ?? GetStr(target, "id")
                          ?? GetStr(item,   "physicalid")
                          ?? GetStr(item,   "id")
                          ?? "";

                var title  = GetStr(target, "title")  ?? GetStr(item, "title")  ?? physId;
                var name   = GetStr(target, "name")   ?? GetStr(item, "name")   ?? "";
                var type   = GetStr(target, "type")   ?? GetStr(item, "type")   ?? "";

                if (string.IsNullOrWhiteSpace(physId)) continue;

                results.Add(new DrawingRef
                {
                    PhysicalId = physId,
                    Title      = title,
                    Name       = name,
                    Type       = type
                });
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "[Drawing] Parsing JSON de la réponse Locate échoué.");
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CSRF
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string?> FetchCsrfAsync(string sc, CancellationToken ct)
    {
        var url = DerivedOutputApiEndpoints.CsrfUrl(_opts.ThreeDSpaceUrl, _opts.Tenant ?? "");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("SecurityContext", sc);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("csrf", out var csrf) &&
                csrf.TryGetProperty("value", out var val))
                return val.GetString();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[Drawing] CSRF non récupéré.");
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string Base() => _opts.ThreeDSpaceUrl.Trim().TrimEnd('/');

    private static string NormalizeSc(string sc) =>
        sc.StartsWith("ctx::", StringComparison.OrdinalIgnoreCase) ? sc["ctx::".Length..] : sc;

    private static string? GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString();
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Référence légère vers une mise en plan (dsxcad:Drawing).</summary>
public sealed record DrawingRef
{
    public string PhysicalId { get; init; } = "";
    public string Title      { get; init; } = "";
    public string Name       { get; init; } = "";
    public string Type       { get; init; } = "";
}
