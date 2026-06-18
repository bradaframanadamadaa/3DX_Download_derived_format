namespace DerivedOutputDownloader3DX.Models;

/// <summary>Job d'export derived output — statut à mapper depuis la réponse REST dsdo.</summary>
public sealed class DerivedOutputExportJob
{
    public string JobId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? ResultDownloadUrl { get; init; }
}
