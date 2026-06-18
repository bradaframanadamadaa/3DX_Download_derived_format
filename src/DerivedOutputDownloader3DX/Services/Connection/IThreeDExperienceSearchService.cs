using DerivedOutputDownloader3DX.Models;

namespace DerivedOutputDownloader3DX.Services;

public interface IThreeDExperienceSearchService
{
    Task<IReadOnlyList<ThreeDxSearchCandidate>> SearchByTitleAsync(
        ThreeDxSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThreeDxSearchCandidate>> SearchByNameAsync(
        ThreeDxSearchQuery query,
        CancellationToken cancellationToken = default);
}

