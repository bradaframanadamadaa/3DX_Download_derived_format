using System.Text.Json;

namespace DerivedOutputDownloader3DX.Configuration;

/// <summary>
/// Construit les URLs cloud 3DEXPERIENCE standard Ã  partir dâ€™un identifiant tenant et dâ€™un segment Â« organisation Â»
/// pour le <see cref="ThreeExperienceOptions.SecurityContext"/> (rÃ´le + org + espace collaboratif).
/// </summary>
public static class ThreeExperienceTenantDefaults
{
    /// <summary>RÃ´le par dÃ©faut pour le contexte de sÃ©curitÃ© (Ã  ajuster si votre cloud exige un autre libellÃ© exact).</summary>
    public const string DefaultSecurityRole = "Responsable";

    /// <summary>Espace collaboratif par dÃ©faut (libellÃ© courant cÃ´tÃ© plateforme).</summary>
    public const string DefaultCollaborativeSpace = "Common Space";

    /// <summary>IAM Â« global Â» (secours si impossible de dÃ©river lâ€™hÃ´te tenant+pod).</summary>
    public const string DefaultPassportBaseUrlGlobal = "https://iam.3dexperience.3ds.com";

    /// <summary>Pod gÃ©ographique par dÃ©faut dans lâ€™hÃ´te (ex. eu1, us1).</summary>
    public const string DefaultGeoPod = "eu1";

    /// <summary>
    /// Copie les options chargÃ©es depuis JSON puis impose URLs + tenant + SecurityContext + CAS (sans secrets).
    /// </summary>
    /// <param name="template">Valeurs non sensibles lues depuis appsettings (ex. SearchTop).</param>
    /// <param name="tenantId">Identifiant tenant (ex. R1132102597931 ou r1132102597931).</param>
    /// <param name="geoPod">Segment gÃ©o dans lâ€™hÃ´te (ex. eu1).</param>
    /// <param name="organizationName">Segment organisation du SecurityContext (entre rÃ´le et espace collaboratif).</param>
    /// <param name="role">RÃ´le ; si vide, <see cref="DefaultSecurityRole"/>.</param>
    /// <param name="collaborativeSpace">Espace collaboratif ; si vide, <see cref="DefaultCollaborativeSpace"/>.</param>
    /// <param name="passportBaseUrl">URL de base Passport pour CAS ; si vide, dÃ©rivation <c>https://{{tenant}}-{{pod}}.iam.3dexperience.3ds.com</c> ou celle du template.</param>
    public static ThreeExperienceOptions BuildInteractiveCloudOptions(
        ThreeExperienceOptions template,
        string tenantId,
        string geoPod,
        string organizationName,
        string? role,
        string? collaborativeSpace,
        string? passportBaseUrl)
    {
        var tenant = NormalizeTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(tenant))
        {
            throw new ArgumentException("Tenant vide.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(organizationName))
        {
            throw new ArgumentException("Organisation vide.", nameof(organizationName));
        }

        var geo = string.IsNullOrWhiteSpace(geoPod) ? DefaultGeoPod : geoPod.Trim().ToLowerInvariant();
        var tenantLower = tenant.ToLowerInvariant();
        // Plateforme IFWE : utilisÃ©e comme Origin FedSearch et comme URL Â« service Â» CAS (cf. logs 3DPassport rÃ©ussis).
        var ifwePlatform = $"https://{tenantLower}-{geo}-ifwe.3dexperience.3ds.com";
        var spaceRoot = $"https://{tenantLower}-{geo}-space.3dexperience.3ds.com";
        var threeDSpace = $"{spaceRoot}/enovia";
        var fedSearch = $"https://{tenantLower}-{geo}-fedsearch.3dexperience.3ds.com";
        var iamRegional = $"https://{tenantLower}-{geo}.iam.3dexperience.3ds.com";

        var rolePart = string.IsNullOrWhiteSpace(role) ? DefaultSecurityRole : role.Trim();
        var collabPart = string.IsNullOrWhiteSpace(collaborativeSpace)
            ? DefaultCollaborativeSpace
            : collaborativeSpace.Trim();
        var orgPart = organizationName.Trim();
        var securityContext = $"{rolePart}.{orgPart}.{collabPart}";

        var json = JsonSerializer.Serialize(template);
        var o = JsonSerializer.Deserialize<ThreeExperienceOptions>(json) ?? new ThreeExperienceOptions();

        o.Tenant = tenant.ToUpperInvariant();
        o.PlatformUrl = ifwePlatform;
        o.ThreeDSpaceUrl = threeDSpace;
        o.FedSearchUrl = fedSearch;
        o.SecurityContext = securityContext;
        o.CasServiceUrl = ifwePlatform.TrimEnd('/') + "/";
        o.ForceCasAuthentication = true;

        var passport = !string.IsNullOrWhiteSpace(passportBaseUrl?.Trim())
            ? passportBaseUrl!.Trim()
            : (!string.IsNullOrWhiteSpace(o.PassportBaseUrl?.Trim()) ? o.PassportBaseUrl.Trim() : iamRegional);
        o.PassportBaseUrl = passport;

        return o;
    }

    private static string NormalizeTenantId(string tenantId)
    {
        var t = tenantId.Trim();
        if (t.Length == 0)
        {
            return "";
        }

        // Retire espaces ; conserve prÃ©fixe R et chiffres tels quels pour lâ€™URL (minuscule appliquÃ©e plus tard).
        return t.Replace(" ", "", StringComparison.Ordinal);
    }
}

