namespace DerivedOutputDownloader3DX.Models;

/// <summary>Métadonnées d'un format dérivé (Exchange ou Internal).</summary>
public sealed class DerivedOutputDescriptor
{
    /// <summary>Id du fichier dérivé (DerivedOutputFiles entry).</summary>
    public string Id { get; init; } = "";

    /// <summary>Id de l'entité DerivedOutputs parente — requis pour DownloadTicket.</summary>
    public string ParentId { get; init; } = "";

    public string Format { get; init; } = "";
    public string FileName { get; init; } = "";
    public bool IsExchangeFormat { get; init; }

    /// <summary>URL FCS directe si déjà fournie par l'API (rare).</summary>
    public string? DownloadUrl { get; init; }
}
