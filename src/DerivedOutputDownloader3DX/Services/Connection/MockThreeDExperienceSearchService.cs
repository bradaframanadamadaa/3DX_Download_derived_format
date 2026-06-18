using DerivedOutputDownloader3DX.Models;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.Services;

/// <summary>Recherche simulÃ©e pour valider Excel / matching sans appel HTTP.</summary>
public sealed class MockThreeDExperienceSearchService : IThreeDExperienceSearchService
{
    private readonly ILogger<MockThreeDExperienceSearchService> _log;

    public MockThreeDExperienceSearchService(ILogger<MockThreeDExperienceSearchService> log)
    {
        _log = log;
    }

    public Task<IReadOnlyList<ThreeDxSearchCandidate>> SearchByTitleAsync(
        ThreeDxSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        _log.LogInformation("[Mock 3DX] Recherche titre « {Title} »", query.Title);
        return SearchMockAsync(query.Title.Trim(), cancellationToken);
    }

    public Task<IReadOnlyList<ThreeDxSearchCandidate>> SearchByNameAsync(
        ThreeDxSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        _log.LogInformation("[Mock 3DX] Recherche nom « {Name} »", query.Name);
        return SearchMockAsync(query.Name.Trim(), cancellationToken);
    }

    private Task<IReadOnlyList<ThreeDxSearchCandidate>> SearchMockAsync(
        string term,
        CancellationToken cancellationToken = default)
    {
        var t = term;
        if (t.Contains("AMBIG", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<ThreeDxSearchCandidate> list = new[]
            {
                NewFake("id-amb-1", t),
                NewFake("id-amb-2", t)
            };
            return Task.FromResult(list);
        }

        if (t.Contains("MATCH", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<ThreeDxSearchCandidate> list = new[] { NewFake("id-mock-1", t) };
            return Task.FromResult(list);
        }

        return Task.FromResult<IReadOnlyList<ThreeDxSearchCandidate>>(Array.Empty<ThreeDxSearchCandidate>());
    }

    private static ThreeDxSearchCandidate NewFake(string id, string title) =>
        new()
        {
            Id = id,
            Type = "MockEngItem",
            Title = title,
            Name = title + "_Name",
            Revision = "A",
            MaturityState = "In Work",
            Owner = "mock.user",
            CollaborativeSpace = "MockSpace",
            Url = "https://example.invalid/mock",
            ConfidenceScore = 95
        };
}

