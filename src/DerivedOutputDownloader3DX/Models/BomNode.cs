namespace DerivedOutputDownloader3DX.Models;

/// <summary>
/// Nœud d'arborescence BOM renvoyé par l'expansion d'un assemblage (dseng:EngItem/expand).
/// Contient l'ID de la pièce référencée (physicalId), pas de l'instance.
/// </summary>
public sealed class BomNode
{
    /// <summary>physicalId de la pièce (VPMReference), à passer à ListDerivedOutputsAsync.</summary>
    public string PhysicalId { get; init; } = "";
    public string Name       { get; init; } = "";
    public string Type       { get; init; } = "";
    public int    Level      { get; init; }

    /// <summary>
    /// true = pièce feuille (aucun enfant VPMReference → téléchargeable).
    /// false = sous-assemblage (a des enfants → sert uniquement à l'affichage de la structure).
    /// </summary>
    public bool IsLeaf { get; init; } = true;
}
