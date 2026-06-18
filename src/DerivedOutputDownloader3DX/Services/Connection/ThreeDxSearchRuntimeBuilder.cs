using DerivedOutputDownloader3DX.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.Services;

/// <summary>
/// Construit le client FedSearch (Bearer ou CAS) selon la mÃªme logique que lâ€™outil console.
/// </summary>
public sealed class ThreeDxSearchRuntime : IDisposable
{
    public required IThreeDExperienceSearchService Service { get; init; }
    public required string Mode { get; init; }
    public IDisposable? DisposableSearch { get; init; }
    public IDisposable? DisposableCas { get; init; }

    public void Dispose()
    {
        DisposableSearch?.Dispose();
        DisposableCas?.Dispose();
    }
}

public static class ThreeDxSearchRuntimeBuilder
{
    public const int ExitThreeDxConfiguration = 7;
    public const int ExitCasLoginFailed = 8;

    /// <param name="casUsernameOverride">Si renseignÃ© avec <paramref name="casPasswordOverride"/>, utilisÃ© Ã  la place des variables dâ€™environnement pour le login CAS (secret non stockÃ© dans le JSON).</param>
    /// <param name="casPasswordOverride">Mot de passe CAS (ne jamais journaliser).</param>
    public static async Task<(ThreeDxSearchRuntime? Runtime, int? ExitCode)> BuildAsync(
        AppOptions appOpts,
        ThreeExperienceOptions threeOpts,
        ILoggerFactory loggerFactory,
        ILogger log,
        CancellationToken cancellationToken,
        string? casUsernameOverride = null,
        string? casPasswordOverride = null)
    {
        var useMock = appOpts.UseMockSearch || appOpts.DryRun;
        if (useMock)
        {
            log.LogInformation("Recherche 3DEXPERIENCE : mode **MOCK** (`DryRun` ou `UseMockSearch`). Aucun HTTP 3DSpace.");
            return (new ThreeDxSearchRuntime
            {
                Service = new MockThreeDExperienceSearchService(
                    loggerFactory.CreateLogger<MockThreeDExperienceSearchService>()),
                Mode = "MOCK"
            }, null);
        }

        var bearerEnv = string.IsNullOrWhiteSpace(threeOpts.BearerTokenEnvironmentVariable)
            ? "THREE_DX_BEARER_TOKEN"
            : threeOpts.BearerTokenEnvironmentVariable;
        var token = Environment.GetEnvironmentVariable(bearerEnv);
        if (!string.IsNullOrWhiteSpace(casUsernameOverride) && !string.IsNullOrWhiteSpace(casPasswordOverride))
        {
            token = null;
            log.LogInformation(
                "Identifiants CAS fournis explicitement : le jeton Bearer ({BearerEnv}) est ignorÃ© pour cette session.",
                bearerEnv);
        }

        var forceCas = threeOpts.ForceCasAuthentication;
        if (forceCas && !string.IsNullOrWhiteSpace(token))
        {
            log.LogWarning(
                "Le jeton Bearer est dÃ©fini dans {BearerEnv} mais **nâ€™est pas utilisÃ©** car `ForceCasAuthentication` est true.",
                bearerEnv);
            token = null;
        }

        var passportEnvVar = string.IsNullOrWhiteSpace(threeOpts.PassportBaseUrlEnvironmentVariable)
            ? "THREE_DX_PASSPORT_URL"
            : threeOpts.PassportBaseUrlEnvironmentVariable;
        var passportFromConfig = threeOpts.PassportBaseUrl?.Trim() ?? "";
        var passportFromEnv = Environment.GetEnvironmentVariable(passportEnvVar)?.Trim() ?? "";
        var passportUrl = !string.IsNullOrWhiteSpace(passportFromConfig) ? passportFromConfig : passportFromEnv;

        var userEnv = string.IsNullOrWhiteSpace(threeOpts.UsernameEnvironmentVariable)
            ? "THREE_DX_USERNAME"
            : threeOpts.UsernameEnvironmentVariable;
        var passEnv = string.IsNullOrWhiteSpace(threeOpts.PasswordEnvironmentVariable)
            ? "THREE_DX_PASSWORD"
            : threeOpts.PasswordEnvironmentVariable;
        var username = !string.IsNullOrWhiteSpace(casUsernameOverride)
            ? casUsernameOverride.Trim()
            : Environment.GetEnvironmentVariable(userEnv);
        var password = !string.IsNullOrWhiteSpace(casPasswordOverride)
            ? casPasswordOverride
            : Environment.GetEnvironmentVariable(passEnv);

        var spaceUrlOk = !string.IsNullOrWhiteSpace(threeOpts.ThreeDSpaceUrl) ||
                         !string.IsNullOrWhiteSpace(threeOpts.FedSearchUrl);

        if (spaceUrlOk && !string.IsNullOrWhiteSpace(token))
        {
            log.LogInformation("Recherche 3DEXPERIENCE : mode **FedSearch Bearer** (flux recherche web, ds6w:label).");
            var fedSearchBearer = new FedSearchThreeDExperienceSearchService(threeOpts, token!,
                loggerFactory.CreateLogger<FedSearchThreeDExperienceSearchService>());
            return (new ThreeDxSearchRuntime
            {
                Service = fedSearchBearer,
                Mode = "FEDSEARCH_BEARER",
                DisposableSearch = fedSearchBearer
            }, null);
        }

        if (spaceUrlOk &&
            !string.IsNullOrWhiteSpace(passportUrl) &&
            !string.IsNullOrWhiteSpace(username) &&
            !string.IsNullOrWhiteSpace(password))
        {
            var cas = new ThreeDxCasPassportClient(loggerFactory.CreateLogger<ThreeDxCasPassportClient>());
            try
            {
                await cas.LoginAsync(threeOpts, passportUrl, username, password, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "Ã‰chec **login CAS** 3DPassport â€” **aucune** bascule vers le mock. Corrigez Passport / CasServiceUrl / identifiants. " +
                    "Message : {Message}. Code sortie : {Code}.",
                    ex.Message,
                    ExitCasLoginFailed);
                cas.Dispose();
                return (null, ExitCasLoginFailed);
            }

            var fedSearchCas = new FedSearchThreeDExperienceSearchService(
                threeOpts,
                cas.Http,
                disposeHttp: false,
                bearerToken: null,
                loggerFactory.CreateLogger<FedSearchThreeDExperienceSearchService>());
            log.LogInformation("Recherche 3DEXPERIENCE : mode **FedSearch CAS** (cookies session, ds6w:label).");
            return (new ThreeDxSearchRuntime
            {
                Service = fedSearchCas,
                Mode = "FEDSEARCH_CAS",
                DisposableSearch = fedSearchCas,
                DisposableCas = cas
            }, null);
        }

        log.LogError(
            "Configuration REST **incomplÃ¨te** : `ThreeDSpaceUrl` vide ou ni Bearer ({BearerEnv}) ni trio CAS " +
            "(Passport config/`{PassportEnv}` + {UserEnv} + mot de passe {PassEnv}). " +
            "**Aucune** bascule vers le mock en mode rÃ©el. Code sortie : {Code}.",
            bearerEnv,
            passportEnvVar,
            userEnv,
            passEnv,
            ExitThreeDxConfiguration);
        return (null, ExitThreeDxConfiguration);
    }
}

