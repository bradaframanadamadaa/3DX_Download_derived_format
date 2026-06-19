using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using DerivedOutputDownloader3DX.Services.DerivedOutput;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.Services.Bom;

/// <summary>
/// Développe l'arborescence BOM d'un assemblage 3DEXPERIENCE via
/// POST dseng:EngItem/{id}/expand.
/// Retourne la liste dédoublonnée des physicalIds de pièces feuilles
/// (et sous-assemblages) contenus dans l'assemblage racine.
/// </summary>
public sealed class BomExpander
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ThreeExperienceOptions _opts;
    private readonly ILogger<BomExpander> _log;

    public BomExpander(
        HttpClient http,
        ThreeExperienceOptions opts,
        ILogger<BomExpander> log)
    {
        _http = http;
        _opts = opts;
        _log  = log;
    }

    /// <summary>
    /// Développe récursivement l'arborescence BOM et retourne uniquement les pièces feuilles
    /// (nœuds sans enfants VPMReference). Utilisé par le téléchargement d'assemblage.
    /// Les pièces réutilisées (même physicalId) ne sont traitées qu'une seule fois.
    /// </summary>
    public async Task<IReadOnlyList<BomNode>> ExpandAsync(
        string assemblyId,
        int expandDepth = -1,   // conservé pour compatibilité, non utilisé (récursion naturelle)
        CancellationToken ct = default)
    {
        _log.LogInformation("[BOM] Expand récursif — assemblage racine {Id}", assemblyId);

        var sc      = BuildSecurityContextHeader();
        var csrf    = await FetchCsrfAsync(sc, ct).ConfigureAwait(false);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leaves  = new List<BomNode>();

        await RecurseAsync(assemblyId, sc, csrf, visited, leaves, level: 0, ct)
            .ConfigureAwait(false);

        _log.LogInformation(
            "[BOM] Récursion terminée — {Count} pièce(s) feuille(s) unique(s) collectée(s).", leaves.Count);
        return leaves;
    }

    /// <summary>
    /// Développe récursivement l'arborescence BOM et retourne TOUS les nœuds :
    /// sous-assemblages (IsLeaf=false) ET pièces feuilles (IsLeaf=true).
    /// Utilisé pour l'affichage de la structure complète.
    /// L'ordre est celui d'un parcours en profondeur (DFS).
    /// </summary>
    public async Task<IReadOnlyList<BomNode>> ExpandAllNodesAsync(
        string assemblyId,
        CancellationToken ct = default)
    {
        _log.LogInformation("[BOM] ExpandAllNodes — assemblage racine {Id}", assemblyId);

        var sc       = BuildSecurityContextHeader();
        var csrf     = await FetchCsrfAsync(sc, ct).ConfigureAwait(false);
        var visited  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allNodes = new List<BomNode>();

        await RecurseAllAsync(assemblyId, sc, csrf, visited, allNodes, level: 0, ct)
            .ConfigureAwait(false);

        _log.LogInformation(
            "[BOM] ExpandAllNodes terminé — {Total} nœud(s) ({Assemblies} sous-asm, {Leaves} feuilles).",
            allNodes.Count,
            allNodes.Count(n => !n.IsLeaf),
            allNodes.Count(n => n.IsLeaf));
        return allNodes;
    }

    // ── Récursion ─────────────────────────────────────────────────────────────

    private async Task RecurseAsync(
        string nodeId,
        string sc,
        string? csrf,
        HashSet<string> visited,
        List<BomNode> leaves,
        int level,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var children = await ExpandOneLevelAsync(nodeId, sc, csrf, ct).ConfigureAwait(false);

        if (children.Count == 0)
        {
            // Pas d'enfants VPMReference → c'est une pièce feuille, mais elle a déjà été
            // ajoutée par le niveau parent. Rien à faire ici.
            return;
        }

        foreach (var child in children)
        {
            if (!visited.Add(child.PhysicalId))
                continue; // déjà traité (pièce réutilisée dans plusieurs sous-assemblages)

            // Tente d'expand l'enfant pour savoir s'il est lui-même un sous-assemblage
            var grandChildren = await ExpandOneLevelAsync(child.PhysicalId, sc, csrf, ct)
                                    .ConfigureAwait(false);

            if (grandChildren.Count == 0)
            {
                // Aucun enfant → pièce feuille → on l'ajoute
                _log.LogDebug(
                    "[BOM] Feuille [{Level}] {Id} ({Name})", level + 1, child.PhysicalId, child.Name);
                leaves.Add(new BomNode
                {
                    PhysicalId = child.PhysicalId,
                    Name       = child.Name,
                    Type       = child.Type,
                    Level      = level + 1
                });
            }
            else
            {
                // A des enfants → sous-assemblage → on recurse sans l'ajouter comme feuille
                _log.LogDebug(
                    "[BOM] Sous-assemblage [{Level}] {Id} ({Name}) → {Count} enfant(s)",
                    level + 1, child.PhysicalId, child.Name, grandChildren.Count);
                await RecurseAsync(child.PhysicalId, sc, csrf, visited, leaves, level + 1, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    // ── Récursion TOUS nœuds (sous-assemblages + feuilles) ───────────────────

    private async Task RecurseAllAsync(
        string nodeId,
        string sc,
        string? csrf,
        HashSet<string> visited,
        List<BomNode> allNodes,
        int level,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var children = await ExpandOneLevelAsync(nodeId, sc, csrf, ct).ConfigureAwait(false);

        foreach (var child in children)
        {
            if (!visited.Add(child.PhysicalId))
                continue;

            var grandChildren = await ExpandOneLevelAsync(child.PhysicalId, sc, csrf, ct)
                                    .ConfigureAwait(false);
            var isLeaf = grandChildren.Count == 0;

            allNodes.Add(new BomNode
            {
                PhysicalId = child.PhysicalId,
                Name       = child.Name,
                Type       = child.Type,
                Level      = level + 1,
                IsLeaf     = isLeaf
            });

            if (!isLeaf)
            {
                _log.LogDebug(
                    "[BOM] Sous-assemblage [{Level}] {Id} ({Name}) → {Count} enfant(s)",
                    level + 1, child.PhysicalId, child.Name, grandChildren.Count);
                await RecurseAllAsync(child.PhysicalId, sc, csrf, visited, allNodes, level + 1, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                _log.LogDebug(
                    "[BOM] Feuille [{Level}] {Id} ({Name})", level + 1, child.PhysicalId, child.Name);
            }
        }
    }

    // ── Appel API un niveau ───────────────────────────────────────────────────

    private async Task<IReadOnlyList<BomNode>> ExpandOneLevelAsync(
        string nodeId,
        string sc,
        string? csrf,
        CancellationToken ct)
    {
        var url = $"{_opts.ThreeDSpaceUrl.TrimEnd('/')}/resources/v1/modeler/dseng/dseng:EngItem" +
                  $"/{Uri.EscapeDataString(nodeId)}/expand" +
                  $"?tenant={Uri.EscapeDataString(_opts.Tenant ?? "")}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("SecurityContext", sc);
        if (!string.IsNullOrWhiteSpace(csrf))
            request.Headers.TryAddWithoutValidation("ENO_CSRF_TOKEN", csrf);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogDebug(
                "[BOM] ExpandOneLevel {Id} — HTTP {Code} (pas de derived outputs attendus)",
                nodeId, (int)response.StatusCode);
            return Array.Empty<BomNode>();
        }

        return ParseExpandResponse(body, nodeId);
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    private IReadOnlyList<BomNode> ParseExpandResponse(string json, string assemblyId)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<BomNode>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement members;
            if (root.ValueKind == JsonValueKind.Array)
                members = root;
            else if (!root.TryGetProperty("member", out members) ||
                     members.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning("[BOM] Structure JSON inattendue (ni tableau ni 'member').");
                return results;
            }

            // L'API expand retourne un tableau PLAT mixant :
            //   - VPMReference  → les pièces/sous-assemblages réels  (on veut leur id)
            //   - VPMInstance   → les occurrences dans la structure    (à ignorer)
            // L'assemblage racine lui-même est aussi présent — on l'exclut.
            foreach (var item in members.EnumerateArray())
            {
                var type = GetStr(item, "type") ?? "";
                var id   = GetStr(item, "id") ?? GetStr(item, "physicalid") ?? GetStr(item, "pid") ?? "";

                // On ne garde que les VPMReference (pas les instances, pas la racine)
                if (!type.Equals("VPMReference", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (id.Equals(assemblyId, StringComparison.OrdinalIgnoreCase))
                    continue; // c'est l'assemblage racine lui-même
                if (!seen.Add(id))
                    continue; // dédoublonnage

                var name  = GetStr(item, "title") ?? GetStr(item, "name") ?? "";
                var level = GetInt(item, "depth") ?? GetInt(item, "level") ?? 1;

                results.Add(new BomNode
                {
                    PhysicalId = id,
                    Name       = name,
                    Type       = type,
                    Level      = level
                });
            }

            _log.LogInformation(
                "[BOM] {Total} membre(s) dans la réponse, {Unique} VPMReference(s) unique(s) (hors racine).",
                members.GetArrayLength(), results.Count);
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "[BOM] Impossible de parser la réponse expand.");
        }

        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string BuildSecurityContextHeader()
    {
        var sc = _opts.SecurityContext?.Trim() ?? "";
        // Retirer le préfixe "ctx::" si présent
        return sc.StartsWith("ctx::", StringComparison.OrdinalIgnoreCase)
            ? sc["ctx::".Length..]
            : sc;
    }

    private async Task<string?> FetchCsrfAsync(string securityContext, CancellationToken ct)
    {
        var url = DerivedOutputApiEndpoints.CsrfUrl(
            _opts.ThreeDSpaceUrl,
            _opts.Tenant ?? "");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("SecurityContext", securityContext);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var respBody   = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(respBody);
            if (doc.RootElement.TryGetProperty("csrf", out var csrf) &&
                csrf.TryGetProperty("value", out var val))
                return val.GetString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BOM] Impossible de récupérer le token CSRF.");
        }

        return null;
    }

    private static string? GetStr(JsonElement el, string prop1, string? prop2 = null)
    {
        if (el.TryGetProperty(prop1, out var p1))
        {
            if (prop2 == null)
                return p1.ValueKind == JsonValueKind.String ? p1.GetString() : null;
            if (p1.TryGetProperty(prop2, out var p2))
                return p2.ValueKind == JsonValueKind.String ? p2.GetString() : null;
        }
        return null;
    }

    private static int? GetInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var p) && p.TryGetInt32(out var v))
            return v;
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
