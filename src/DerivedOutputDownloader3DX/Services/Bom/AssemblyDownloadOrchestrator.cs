using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.Services.Bom;

/// <summary>
/// Orchestre le téléchargement de tous les formats dérivés d'un assemblage :
///   1. Développe le BOM (BomExpander)
///   2. Pour chaque pièce unique, liste les derived outputs
///   3. Filtre par formats souhaités
///   4. Retourne une liste de jobs prête à être exécutée
/// </summary>
public sealed class AssemblyDownloadOrchestrator
{
    private readonly BomExpander _bom;
    private readonly DerivedOutputClient _derivedClient;
    private readonly ILogger<AssemblyDownloadOrchestrator> _log;

    public AssemblyDownloadOrchestrator(
        BomExpander bom,
        DerivedOutputClient derivedClient,
        ILogger<AssemblyDownloadOrchestrator> log)
    {
        _bom           = bom;
        _derivedClient = derivedClient;
        _log           = log;
    }

    /// <summary>
    /// Développe l'assemblage et retourne tous les descripteurs de fichiers dérivés
    /// correspondant aux formats demandés.
    /// </summary>
    /// <param name="assemblyId">physicalId de l'assemblage racine.</param>
    /// <param name="formatFilter">
    /// Formats à inclure (ex. { "PDF", "STEP_AP214" }).
    /// Si vide ou null, tous les formats sont inclus.
    /// </param>
    /// <param name="progress">Callback de progression (message texte).</param>
    public async Task<IReadOnlyList<AssemblyDownloadJob>> ResolveJobsAsync(
        string assemblyId,
        IReadOnlyCollection<string>? formatFilter = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Développement du BOM en cours…");

        var nodes = await _bom.ExpandAsync(assemblyId, expandDepth: -1, ct)
                              .ConfigureAwait(false);

        _log.LogInformation(
            "[Orchestrator] BOM développé — {Count} pièce(s) unique(s).", nodes.Count);
        progress?.Report($"BOM développé : {nodes.Count} pièce(s). Récupération des formats dérivés…");

        var jobs = new List<AssemblyDownloadJob>();

        // On parcourt séquentiellement pour ne pas surcharger l'API
        for (var i = 0; i < nodes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var node = nodes[i];
            progress?.Report(
                $"[{i + 1}/{nodes.Count}] Analyse de « {node.Name} »…");

            try
            {
                var descriptors = await _derivedClient
                    .ListDerivedOutputsAsync(node.PhysicalId, ct)
                    .ConfigureAwait(false);

                foreach (var desc in descriptors)
                {
                    if (formatFilter != null &&
                        formatFilter.Count > 0 &&
                        !formatFilter.Contains(desc.Format, StringComparer.OrdinalIgnoreCase))
                        continue;

                    jobs.Add(new AssemblyDownloadJob
                    {
                        Node       = node,
                        Descriptor = desc
                    });
                }

                _log.LogDebug(
                    "[Orchestrator] {Name} → {Count} descripteur(s) retenus.",
                    node.Name, jobs.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[Orchestrator] Impossible de récupérer les formats de {Id} ({Name}).",
                    node.PhysicalId, node.Name);
            }
        }

        _log.LogInformation(
            "[Orchestrator] Résolution terminée — {Count} fichier(s) à télécharger.", jobs.Count);
        progress?.Report(
            $"Résolution terminée : {jobs.Count} fichier(s) à télécharger.");

        return jobs;
    }
}

/// <summary>Un téléchargement résolu : nœud BOM + descripteur de fichier dérivé.</summary>
public sealed class AssemblyDownloadJob
{
    public BomNode                  Node       { get; init; } = new();
    public DerivedOutputDescriptor  Descriptor { get; init; } = new();
}
