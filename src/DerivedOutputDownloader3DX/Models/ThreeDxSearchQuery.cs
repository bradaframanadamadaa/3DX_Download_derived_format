namespace DerivedOutputDownloader3DX.Models;

/// <summary>Paramètres d'une recherche côté 3DSpace (mock ou REST).</summary>
public sealed record ThreeDxSearchQuery(
    string PlatformUrl = "",
    string ThreeDSpaceUrl = "",
    string? SecurityContext = null,
    string Title = "",
    string Name = "");

