using System.Text;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using DerivedOutputDownloader3DX.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitArgError = 2;
    private const int ExitSecurityContextInvalid = 4;
    private const int ExitConnectionProbeFailed = 9;

    private static async Task<int> Main(string[] args)
    {
        var parsed = ParseArgs(args);
        if (parsed.Help)
        {
            PrintHelp(Console.Out);
            return ExitOk;
        }

        var settingsPath = ResolveSettingsPath(parsed.SettingsPath);
        if (settingsPath == null)
        {
            return ExitArgError;
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(settingsPath)!)
            .AddJsonFile(Path.GetFileName(settingsPath), optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var appOpts = configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
        var threeOpts = configuration.GetSection(ThreeExperienceOptions.SectionName).Get<ThreeExperienceOptions>()
                        ?? new ThreeExperienceOptions();

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddConsole();
        });
        var log = loggerFactory.CreateLogger("DerivedOutputDownloader3DX");

        if (parsed.ConnectionOnly)
        {
            return await RunConnectionOnlyAsync(appOpts, threeOpts, parsed, loggerFactory, log)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(parsed.SearchTitle))
        {
            return await RunSearchAsync(
                    appOpts,
                    threeOpts,
                    parsed,
                    loggerFactory,
                    log,
                    byName: false,
                    parsed.SearchTitle.Trim())
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(parsed.SearchName))
        {
            return await RunSearchAsync(
                    appOpts,
                    threeOpts,
                    parsed,
                    loggerFactory,
                    log,
                    byName: true,
                    parsed.SearchName.Trim())
                .ConfigureAwait(false);
        }

        if (parsed.ListContexts)
        {
            return await RunListContextsAsync(appOpts, threeOpts, parsed, loggerFactory, log)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(parsed.ObjectId))
        {
            return await RunDerivedOutputAsync(appOpts, threeOpts, parsed, loggerFactory, log)
                .ConfigureAwait(false);
        }

        log.LogError("Arguments insuffisants. Utilisez --connection-only, --search-title ou --help.");
        PrintHelp(Console.Out);
        return ExitArgError;
    }

    private static async Task<int> RunConnectionOnlyAsync(
        AppOptions appOpts,
        ThreeExperienceOptions threeOpts,
        ParsedArgs parsed,
        ILoggerFactory loggerFactory,
        ILogger log)
    {
        log.LogInformation(
            "Mode --connection-only : test CAS + FedSearch (ignore DryRun / UseMockSearch pour ce test).");

        if (IsSecurityContextInvalid(threeOpts.SecurityContext))
        {
            log.LogError(
                "SecurityContext absent ou placeholder A_COMPLETER. Complétez appsettings.json. Code sortie : {Code}.",
                ExitSecurityContextInvalid);
            return ExitSecurityContextInvalid;
        }

        if (!TryEnsureCasOrBearerReady(threeOpts, parsed, Console.Out, out var casUser, out var casPass))
        {
            return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;
        }

        var probeAppOpts = new AppOptions
        {
            DryRun = false,
            UseMockSearch = false,
            OutputDirectory = appOpts.OutputDirectory
        };
        var (runtime, exitCode) = await ThreeDxSearchRuntimeBuilder.BuildAsync(
            probeAppOpts,
            threeOpts,
            loggerFactory,
            log,
            CancellationToken.None,
            casUsernameOverride: casUser,
            casPasswordOverride: casPass).ConfigureAwait(false);

        if (exitCode is int code)
        {
            return code;
        }

        if (runtime is null)
        {
            return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;
        }

        using (runtime)
        {
            if (runtime.Service is not FedSearchThreeDExperienceSearchService fedSearch)
            {
                log.LogError("Service FedSearch attendu après connexion CAS.");
                return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;
            }

            var probeTitle = string.IsNullOrWhiteSpace(threeOpts.ConnectionProbeTitle)
                ? "manivelle"
                : threeOpts.ConnectionProbeTitle.Trim();
            var result = await fedSearch.ProbeTitleSearchAsync(probeTitle, CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                log.LogError(
                    "Test FedSearch échec. Endpoint : {Endpoint} | HTTP : {Http} | Détail : {Detail}. Code : {Code}.",
                    result.Endpoint,
                    result.HttpStatus?.ToString() ?? "(n/a)",
                    result.Detail ?? "",
                    ExitConnectionProbeFailed);
                return ExitConnectionProbeFailed;
            }

            log.LogInformation("Connexion 3DEXPERIENCE OK ({Mode}). {Detail}", runtime.Mode, result.Detail ?? "");
            return ExitOk;
        }
    }

    private static async Task<int> RunSearchAsync(
        AppOptions appOpts,
        ThreeExperienceOptions threeOpts,
        ParsedArgs parsed,
        ILoggerFactory loggerFactory,
        ILogger log,
        bool byName,
        string term)
    {
        if (IsSecurityContextInvalid(threeOpts.SecurityContext))
        {
            log.LogError(
                "SecurityContext absent ou placeholder A_COMPLETER. Code sortie : {Code}.",
                ExitSecurityContextInvalid);
            return ExitSecurityContextInvalid;
        }

        string? casUser = null;
        string? casPass = null;
        if (!appOpts.UseMockSearch && !appOpts.DryRun)
        {
            if (!TryEnsureCasOrBearerReady(threeOpts, parsed, Console.Out, out casUser, out casPass))
            {
                return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;
            }
        }

        var (runtime, exitCode) = await ThreeDxSearchRuntimeBuilder.BuildAsync(
            appOpts,
            threeOpts,
            loggerFactory,
            log,
            CancellationToken.None,
            casUsernameOverride: casUser,
            casPasswordOverride: casPass).ConfigureAwait(false);

        if (exitCode is int code)
        {
            return code;
        }

        if (runtime is null)
        {
            return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;
        }

        using (runtime)
        {
            var query = byName
                ? new ThreeDxSearchQuery(
                    PlatformUrl: threeOpts.PlatformUrl,
                    ThreeDSpaceUrl: threeOpts.ThreeDSpaceUrl,
                    SecurityContext: threeOpts.SecurityContext,
                    Name: term)
                : new ThreeDxSearchQuery(
                    PlatformUrl: threeOpts.PlatformUrl,
                    ThreeDSpaceUrl: threeOpts.ThreeDSpaceUrl,
                    SecurityContext: threeOpts.SecurityContext,
                    Title: term);

            IReadOnlyList<ThreeDxSearchCandidate> candidates;
            try
            {
                candidates = byName
                    ? await runtime.Service.SearchByNameAsync(query, CancellationToken.None).ConfigureAwait(false)
                    : await runtime.Service.SearchByTitleAsync(query, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                log.LogError(
                    "FedSearch échec pour « {Term} » ({Field}) : {Detail}. Code : {Code}.",
                    term,
                    byName ? "nom/ds6w:identifier" : "titre/ds6w:label",
                    ex.Message,
                    ExitConnectionProbeFailed);
                return ExitConnectionProbeFailed;
            }
            catch (HttpRequestException ex)
            {
                log.LogError(
                    ex,
                    "FedSearch échec réseau pour « {Term} » ({Field}). Code : {Code}.",
                    term,
                    byName ? "nom/ds6w:identifier" : "titre/ds6w:label",
                    ExitConnectionProbeFailed);
                return ExitConnectionProbeFailed;
            }

            var fieldLabel = byName ? "nom" : "titre";
            log.LogInformation("FedSearch : {Count} candidat(s) pour le {Field} « {Term} ».", candidates.Count, fieldLabel, term);
            foreach (var c in candidates)
            {
                log.LogInformation(
                    "  Id={Id} | Name={Name} | Type={Type} | Title={Title} | Rev={Rev} | State={State} | Owner={Owner}",
                    c.Id,
                    c.Name,
                    c.Type,
                    c.Title,
                    c.Revision,
                    c.MaturityState,
                    c.Owner);
            }

            return ExitOk;
        }
    }

    private static async Task<int> RunListContextsAsync(
        AppOptions appOpts,
        ThreeExperienceOptions threeOpts,
        ParsedArgs parsed,
        ILoggerFactory loggerFactory,
        ILogger log)
    {
        if (!TryEnsureCasOrBearerReady(threeOpts, parsed, Console.Out, out var casUser, out var casPass))
        {
            return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;
        }

        // On veut que le SecurityContext soit découvert — on met un placeholder pour forcer l'auto-detect.
        var optsForDiscover = new ThreeExperienceOptions
        {
            PlatformUrl = threeOpts.PlatformUrl,
            ThreeDSpaceUrl = threeOpts.ThreeDSpaceUrl,
            FedSearchUrl = threeOpts.FedSearchUrl,
            Tenant = threeOpts.Tenant,
            PassportBaseUrl = threeOpts.PassportBaseUrl,
            CasServiceUrl = threeOpts.CasServiceUrl,
            SecurityContext = "",           // force la découverte via pno/person
            ForceCasAuthentication = true,
            SearchTop = threeOpts.SearchTop,
            UsernameEnvironmentVariable = threeOpts.UsernameEnvironmentVariable,
            PasswordEnvironmentVariable = threeOpts.PasswordEnvironmentVariable,
            BearerTokenEnvironmentVariable = threeOpts.BearerTokenEnvironmentVariable,
            PassportBaseUrlEnvironmentVariable = threeOpts.PassportBaseUrlEnvironmentVariable
        };

        var cas = new ThreeDxCasPassportClient(loggerFactory.CreateLogger<ThreeDxCasPassportClient>());
        try
        {
            var passport = threeOpts.PassportBaseUrl?.Trim() ?? "";
            await cas.LoginAsync(optsForDiscover, passport, casUser!, casPass!, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Échec login CAS pour --list-contexts.");
            cas.Dispose();
            return ThreeDxSearchRuntimeBuilder.ExitCasLoginFailed;
        }

        using (cas)
        {
            var dsdoLog = loggerFactory.CreateLogger<DerivedOutputClient>();
            var client = new DerivedOutputClient(cas.Http, optsForDiscover, dsdoLog);

            IReadOnlyList<string> contexts;
            try
            {
                contexts = await client.ListAvailableSecurityContextsAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Impossible de récupérer les SecurityContexts via pno/person.");
                log.LogInformation(
                    "Cherchez votre SecurityContext dans l'UI 3DEXPERIENCE : " +
                    "clic sur votre avatar → Paramètres → Contexte de sécurité.");
                return ExitConnectionProbeFailed;
            }

            if (contexts.Count == 0)
            {
                log.LogWarning(
                    "Aucun espace collaboratif trouvé pour {User}. " +
                    "Vérifiez la configuration du compte dans 3DEXPERIENCE.", casUser);
                return ExitConnectionProbeFailed;
            }

            log.LogInformation("SecurityContexts disponibles pour {User} ({Count}) :", casUser, contexts.Count);
            for (var i = 0; i < contexts.Count; i++)
            {
                log.LogInformation("  [{Idx}] ctx::{Ctx}", i + 1, contexts[i]);
            }
            log.LogInformation(string.Empty);
            log.LogInformation("→ Copiez la valeur choisie dans appsettings.json :");
            log.LogInformation("     \"SecurityContext\": \"ctx::{Exemple}\"", contexts[0]);
        }

        return ExitOk;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static async Task<int> RunDerivedOutputAsync(
        AppOptions appOpts,
        ThreeExperienceOptions threeOpts,
        ParsedArgs parsed,
        ILoggerFactory loggerFactory,
        ILogger log)
    {
        // SecurityContext peut être un placeholder — DerivedOutputClient le découvre automatiquement via pno/person.
        if (!TryEnsureCasOrBearerReady(threeOpts, parsed, Console.Out, out var casUser, out var casPass))
        {
            return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;
        }

        var (runtime, exitCode) = await ThreeDxSearchRuntimeBuilder.BuildAsync(
            appOpts,
            threeOpts,
            loggerFactory,
            log,
            CancellationToken.None,
            casUsernameOverride: casUser,
            casPasswordOverride: casPass).ConfigureAwait(false);

        if (exitCode is int code) return code;
        if (runtime is null) return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;

        using (runtime)
        {
            // Récupérer le HttpClient CAS porteur des cookies (nécessaire pour dsdo)
            HttpClient? casHttp = null;
            if (runtime.DisposableCas is ThreeDxCasPassportClient casClient)
            {
                casHttp = casClient.Http;
            }

            if (casHttp is null)
            {
                log.LogError(
                    "Mode FEDSEARCH_CAS requis pour les appels dsdo — Bearer non supporté pour ce flux. " +
                    "Vérifiez ForceCasAuthentication=true dans appsettings.json.");
                return ThreeDxSearchRuntimeBuilder.ExitThreeDxConfiguration;
            }

            var dsdoLog = loggerFactory.CreateLogger<DerivedOutputClient>();
            var client = new DerivedOutputClient(casHttp, threeOpts, dsdoLog);

            var physicalId = parsed.ObjectId!.Trim();
            log.LogInformation("[dsdo] Objet cible : {Id}", physicalId);

            IReadOnlyList<DerivedOutputDescriptor> descriptors;
            try
            {
                descriptors = await client.ListDerivedOutputsAsync(physicalId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                log.LogError("Échec liste derived outputs : {Detail}", ex.Message);
                return ExitConnectionProbeFailed;
            }

            if (descriptors.Count == 0)
            {
                log.LogWarning("Aucun derived output trouvé pour l'objet {Id}.", physicalId);
                return ExitOk;
            }

            log.LogInformation("Derived outputs disponibles ({Count}) :", descriptors.Count);
            for (var i = 0; i < descriptors.Count; i++)
            {
                var d = descriptors[i];
                log.LogInformation(
                    "  [{Idx}] Format={Format} | Fichier={File} | Exchange={Ex} | Id={Id}",
                    i + 1,
                    d.Format,
                    d.FileName,
                    d.IsExchangeFormat,
                    d.Id);
            }

            var target = ResolveDownloadTarget(descriptors, parsed, Console.Out);
            if (target is null)
            {
                if (string.IsNullOrWhiteSpace(parsed.DownloadFormat) && parsed.NoInteractive)
                {
                    log.LogInformation(
                        "Ajoutez --format <FORMAT> pour télécharger (ex. --format PDF). Formats disponibles ci-dessus.");
                }

                return string.IsNullOrWhiteSpace(parsed.DownloadFormat) ? ExitOk : ExitArgError;
            }

            var outputDir = ReadOutputDirectory(parsed, appOpts, Console.Out);
            if (outputDir is null)
            {
                return ExitArgError;
            }

            log.LogInformation(
                "[dsdo] Téléchargement {Format} ({File}) vers : {Dir}",
                target.Format,
                target.FileName,
                outputDir);

            string savedPath;
            try
            {
                savedPath = await client.DownloadDerivedOutputAsync(target, outputDir, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                log.LogError("Échec téléchargement : {Detail}", ex.Message);
                return ExitConnectionProbeFailed;
            }

            log.LogInformation("Fichier téléchargé : {Path}", savedPath);
            return ExitOk;
        }
    }

    /// <summary>
    /// Résout le derived output à télécharger : --format CLI, ou sélection interactive.
    /// Retourne null si l'utilisateur annule ou si mode liste seul (--no-interactive sans --format).
    /// </summary>
    private static DerivedOutputDescriptor? ResolveDownloadTarget(
        IReadOnlyList<DerivedOutputDescriptor> descriptors,
        ParsedArgs parsed,
        TextWriter log)
    {
        if (!string.IsNullOrWhiteSpace(parsed.DownloadFormat))
        {
            var wantedFormat = parsed.DownloadFormat.Trim().ToUpperInvariant();
            var fromCli = descriptors.FirstOrDefault(
                d => d.Format.Equals(wantedFormat, StringComparison.OrdinalIgnoreCase));
            if (fromCli is null)
            {
                log.WriteLine(
                    $"ERREUR : format {wantedFormat} non trouvé. Disponibles : " +
                    string.Join(", ", descriptors.Select(d => d.Format)));
            }

            return fromCli;
        }

        if (parsed.NoInteractive)
        {
            return null;
        }

        log.WriteLine(string.Empty);

        if (descriptors.Count == 1)
        {
            var only = descriptors[0];
            log.WriteLine(
                $"Un seul format disponible : {only.Format} ({only.FileName}). Téléchargement proposé.");
            return only;
        }

        log.WriteLine("Quel format télécharger ?");
        for (var i = 0; i < descriptors.Count; i++)
        {
            var d = descriptors[i];
            log.WriteLine($"  [{i + 1}] {d.Format} — {d.FileName}");
        }

        log.Write("Choix (numéro ou nom du format, vide = annuler) : ");
        var entered = Console.In.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(entered))
        {
            log.WriteLine("Téléchargement annulé.");
            return null;
        }

        if (int.TryParse(entered, out var idx) && idx >= 1 && idx <= descriptors.Count)
        {
            return descriptors[idx - 1];
        }

        var byName = descriptors.FirstOrDefault(
            d => d.Format.Equals(entered, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        log.WriteLine($"ERREUR : choix « {entered} » invalide.");
        return null;
    }

    private static string? ReadOutputDirectory(ParsedArgs parsed, AppOptions appOpts, TextWriter log)
    {
        // Priorité : --output-dir CLI > saisie interactive
        if (!string.IsNullOrWhiteSpace(parsed.OutputDir))
        {
            return Path.GetFullPath(parsed.OutputDir.Trim());
        }

        if (parsed.NoInteractive)
        {
            // En mode non interactif : utiliser OutputDirectory de appsettings
            if (!string.IsNullOrWhiteSpace(appOpts.OutputDirectory))
            {
                return Path.GetFullPath(appOpts.OutputDirectory.Trim());
            }

            log.WriteLine("ERREUR : dossier de sortie introuvable. Passez --output-dir <chemin> ou définissez App:OutputDirectory dans appsettings.json.");
            return null;
        }

        log.WriteLine(string.Empty);
        log.Write($"Dossier de sortie (défaut : {appOpts.OutputDirectory ?? "output/"}) : ");
        var entered = Console.In.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(entered))
        {
            entered = appOpts.OutputDirectory?.Trim() ?? "output";
        }

        return Path.GetFullPath(entered);
    }

    /// <summary>
    /// Vérifie URLs 3DEXPERIENCE ; si pas de Bearer utilisable, assure Passport + identifiant + mot de passe CAS
    /// (variables d'environnement ou invite console).
    /// </summary>
    private static bool TryEnsureCasOrBearerReady(
        ThreeExperienceOptions threeOpts,
        ParsedArgs parsed,
        TextWriter log,
        out string? casUsernameOverride,
        out string? casPasswordOverride)
    {
        casUsernameOverride = null;
        casPasswordOverride = null;

        var bearerEnv = string.IsNullOrWhiteSpace(threeOpts.BearerTokenEnvironmentVariable)
            ? "THREE_DX_BEARER_TOKEN"
            : threeOpts.BearerTokenEnvironmentVariable;
        var token = Environment.GetEnvironmentVariable(bearerEnv);

        var forceCas = threeOpts.ForceCasAuthentication;
        if (forceCas && !string.IsNullOrWhiteSpace(token))
        {
            token = null;
        }

        var spaceUrlOk = !string.IsNullOrWhiteSpace(threeOpts.ThreeDSpaceUrl) ||
                         !string.IsNullOrWhiteSpace(threeOpts.FedSearchUrl);
        if (!spaceUrlOk)
        {
            log.WriteLine(
                "ERREUR : `ThreeDSpaceUrl` et `FedSearchUrl` sont vides dans appsettings — " +
                "complétez la section `ThreeExperience`.");
            return false;
        }

        var useBearer = !string.IsNullOrWhiteSpace(token) && !forceCas;
        if (useBearer)
        {
            log.WriteLine($"FedSearch : jeton Bearer ({bearerEnv}) détecté — pas de saisie interactive CAS.");
            return true;
        }

        var passportEnvVar = string.IsNullOrWhiteSpace(threeOpts.PassportBaseUrlEnvironmentVariable)
            ? "THREE_DX_PASSPORT_URL"
            : threeOpts.PassportBaseUrlEnvironmentVariable;
        var passportFromConfig = threeOpts.PassportBaseUrl?.Trim() ?? "";
        var passportFromEnv = Environment.GetEnvironmentVariable(passportEnvVar)?.Trim() ?? "";
        var passportUrl = !string.IsNullOrWhiteSpace(passportFromConfig) ? passportFromConfig : passportFromEnv;

        if (string.IsNullOrWhiteSpace(passportUrl))
        {
            if (parsed.NoInteractive)
            {
                log.WriteLine(
                    $"ERREUR : URL 3DPassport absente (`ThreeExperience:PassportBaseUrl` ou variable {passportEnvVar}). " +
                    "Sans --no-interactive, l'URL peut être saisie au clavier.");
                return false;
            }

            log.WriteLine(string.Empty);
            log.WriteLine("Connexion CAS 3DEXPERIENCE : l'URL 3DPassport est absente (fichier config / variable d'environnement).");
            log.WriteLine($"Exemple : {passportEnvVar}=https://votre-tenant.iam.3dexperience.3ds.com");
            log.Write("URL 3DPassport : ");
            var enteredPassport = Console.In.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(enteredPassport))
            {
                log.WriteLine("ERREUR : URL Passport obligatoire pour le flux CAS.");
                return false;
            }

            threeOpts.PassportBaseUrl = enteredPassport;
        }

        var userEnv = string.IsNullOrWhiteSpace(threeOpts.UsernameEnvironmentVariable)
            ? "THREE_DX_USERNAME"
            : threeOpts.UsernameEnvironmentVariable;
        var passEnv = string.IsNullOrWhiteSpace(threeOpts.PasswordEnvironmentVariable)
            ? "THREE_DX_PASSWORD"
            : threeOpts.PasswordEnvironmentVariable;

        if (parsed.NoInteractive)
        {
            var username = Environment.GetEnvironmentVariable(userEnv);
            var password = Environment.GetEnvironmentVariable(passEnv);
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                log.WriteLine($"FedSearch : identifiants CAS lus depuis les variables d'environnement ({userEnv}).");
                return true;
            }

            log.WriteLine(
                $"ERREUR : identifiants CAS incomplets. Définissez {bearerEnv}, ou {userEnv} + {passEnv}.");
            return false;
        }

        log.WriteLine(string.Empty);
        log.WriteLine("Connexion CAS 3DEXPERIENCE : saisie des identifiants (non journalisés, non stockés).");

        log.Write($"Identifiant ({userEnv}) : ");
        var interactiveUsername = Console.In.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(interactiveUsername))
        {
            log.WriteLine("ERREUR : identifiant vide.");
            return false;
        }

        log.Write($"Mot de passe ({passEnv}) : ");
        var interactivePassword = ReadPasswordMasked();
        if (string.IsNullOrWhiteSpace(interactivePassword))
        {
            log.WriteLine("ERREUR : mot de passe vide.");
            return false;
        }

        casUsernameOverride = interactiveUsername;
        casPasswordOverride = interactivePassword;
        return true;
    }

    private static string ReadPasswordMasked()
    {
        // Console.ReadKey ne fonctionne pas dans un terminal non-TTY (ex. terminal intégré Cursor,
        // stdout redirigé). Dans ce cas on bascule sur ReadLine sans masquage.
        if (!Console.IsInputRedirected)
        {
            var sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(intercept: true);
                }
                catch (InvalidOperationException)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            return sb.ToString();
        }

        // Fallback terminal non-TTY : ReadLine sans masquage
        Console.WriteLine("(mot de passe visible — terminal non interactif)");
        return Console.In.ReadLine()?.Trim() ?? "";
    }

    private static bool IsSecurityContextInvalid(string? securityContext)
    {
        var sc = securityContext?.Trim() ?? "";
        return sc.Length == 0 ||
               sc.Contains("A_COMPLETER", StringComparison.OrdinalIgnoreCase) ||
               sc.Contains("VOTRE_", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSettingsPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var p = Path.GetFullPath(explicitPath.Trim());
            if (!File.Exists(p))
            {
                Console.Error.WriteLine($"ERREUR : fichier de configuration introuvable : {p}");
                return null;
            }

            return p;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "samples", "appsettings.sample.json")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return Path.GetFullPath(c);
            }
        }

        Console.Error.WriteLine(
            "ERREUR : appsettings.json introuvable. Passez --settings <chemin> ou copiez samples/appsettings.sample.json.");
        return null;
    }

    private static ParsedArgs ParseArgs(string[] args)
    {
        var o = new ParsedArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h":
                case "--help":
                    o.Help = true;
                    break;
                case "--connection-only":
                    o.ConnectionOnly = true;
                    break;
                case "--search-title":
                    o.SearchTitle = ReadNext(args, ref i, "--search-title");
                    break;
                case "--search-name":
                    o.SearchName = ReadNext(args, ref i, "--search-name");
                    break;
                case "--settings":
                    o.SettingsPath = ReadNext(args, ref i, "--settings");
                    break;
                case "--object-id":
                    o.ObjectId = ReadNext(args, ref i, "--object-id");
                    break;
                case "--format":
                    o.DownloadFormat = ReadNext(args, ref i, "--format");
                    break;
                case "--output-dir":
                    o.OutputDir = ReadNext(args, ref i, "--output-dir");
                    break;
                case "--list-contexts":
                    o.ListContexts = true;
                    break;
                case "--no-interactive":
                    o.NoInteractive = true;
                    break;
                default:
                    Console.Error.WriteLine($"Argument inconnu : {a}");
                    o.Help = true;
                    break;
            }
        }

        return o;
    }

    private static string ReadNext(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Valeur manquante après {flag}.");
        }

        i++;
        return args[i];
    }

    private static void PrintHelp(TextWriter w)
    {
        w.WriteLine("""
            DerivedOutputDownloader3DX — téléchargement de formats dérivés 3DEXPERIENCE

            Connexion (réutilise CAS + FedSearch, voir CONNEXION_3DEXPERIENCE_ET_API.md) :

              --connection-only          Test CAS + sonde FedSearch (ConnectionProbeTitle)
              --search-title <titre>     Recherche FedSearch par titre (ds6w:label)
              --search-name <nom>        Recherche FedSearch par nom (ds6w:identifier)

            Derived Outputs REST (dsdo) :

              --list-contexts            Lister les SecurityContexts disponibles pour ton compte
              --object-id <id>           Lister les formats, puis proposer le téléchargement
              --object-id <id> --format <PDF|DWG|...>   Télécharger le format demandé
              --output-dir <chemin>      Dossier de sortie (sinon : saisie interactive)

            Configuration :

              --settings <chemin>        Chemin appsettings.json (défaut : appsettings.json à côté de l'exe)
              --no-interactive           Pas d'invite console (CI : exiger Bearer ou env CAS complets)
              -h, --help                 Cette aide

            Connexion CAS :

              Par défaut, login et mot de passe sont demandés au clavier (les variables d'environnement sont ignorées).
              Avec --no-interactive : THREE_DX_USERNAME + THREE_DX_PASSWORD requis
              THREE_DX_PASSPORT_URL                 URL 3DPassport (si absente du JSON)
              THREE_DX_BEARER_TOKEN                 Jeton Bearer (ignoré si ForceCasAuthentication=true)

            Exemples :

              dotnet run -- --connection-only
              dotnet run -- --search-title "Manivelle_Pion"
              dotnet run -- --search-name "drw-R1132102597931-00000003"
            """);
    }

    private sealed class ParsedArgs
    {
        public bool Help { get; set; }
        public bool ConnectionOnly { get; set; }
        public string? SearchTitle { get; set; }
        public string? SearchName { get; set; }
        public string? SettingsPath { get; set; }
        public string? ObjectId { get; set; }
        public string? DownloadFormat { get; set; }
        public string? OutputDir { get; set; }
        public bool ListContexts { get; set; }
        public bool NoInteractive { get; set; }
    }
}
