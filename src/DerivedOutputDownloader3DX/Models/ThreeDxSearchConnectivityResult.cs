namespace DerivedOutputDownloader3DX.Models;

/// <summary>RÃ©sultat dâ€™un test HTTP minimal sur <c>dseng:EngItem/search</c> (mode connexion).</summary>
public readonly record struct ThreeDxSearchConnectivityResult(
    bool Success,
    int? HttpStatus,
    string Endpoint,
    string? Detail);

