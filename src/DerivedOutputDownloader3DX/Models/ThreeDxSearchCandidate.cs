namespace DerivedOutputDownloader3DX.Models;

/// <summary>RÃ©sultat candidat renvoyÃ© par la recherche 3DSpace (mock ou REST).</summary>
public sealed class ThreeDxSearchCandidate
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Name { get; init; } = "";
    public string Revision { get; init; } = "";
    public string MaturityState { get; init; } = "";
    public string Owner { get; init; } = "";
    public string CollaborativeSpace { get; init; } = "";
    public string? Url { get; init; }
    public double ConfidenceScore { get; init; }
}

