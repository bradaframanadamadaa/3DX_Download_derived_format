namespace DerivedOutputDownloader3DX.Configuration;

public sealed class ThreeExperienceOptions
{
    public const string SectionName = "ThreeExperience";

    // ── Seul champ obligatoire ────────────────────────────────────────────────
    // Ex. : "R1132102597931"
    public string Tenant { get; set; } = "";

    // ── URLs dérivées automatiquement si laissées vides ───────────────────────
    public string PlatformUrl    { get; set; } = "";
    public string ThreeDSpaceUrl { get; set; } = "";
    public string FedSearchUrl   { get; set; } = "";
    public string PassportBaseUrl { get; set; } = "";
    public string CasServiceUrl  { get; set; } = "";

    // ── Contexte de sécurité ─────────────────────────────────────────────────
    // Format : ctx::VPLMProjectLeader.Company Name.NOM_ESPACE
    // Si vide, sera dérivé de CollaborativeSpace ci-dessous.
    public string SecurityContext { get; set; } = "";

    // ── Champ de commodité : nom de l'espace collaboratif ────────────────────
    // Ex. : "Common Space" — utilisé uniquement si SecurityContext est vide.
    public string CollaborativeSpace { get; set; } = "Common Space";

    // ── Variables d'environnement (valeurs par défaut, rarement à changer) ───
    public string PassportBaseUrlEnvironmentVariable { get; set; } = "THREE_DX_PASSPORT_URL";
    public string UsernameEnvironmentVariable        { get; set; } = "THREE_DX_USERNAME";
    public string PasswordEnvironmentVariable        { get; set; } = "THREE_DX_PASSWORD";
    public string BearerTokenEnvironmentVariable     { get; set; } = "THREE_DX_BEARER_TOKEN";

    // ── Divers ────────────────────────────────────────────────────────────────
    public int    SearchTop              { get; set; } = 50;
    public string ConnectionProbeTitle   { get; set; } = "";
    public bool   ForceCasAuthentication { get; set; } = true;

    // ── Construction automatique des URLs depuis le Tenant ───────────────────

    /// <summary>
    /// Complète les URLs vides à partir de <see cref="Tenant"/>.
    /// Le tenant est normalisé en minuscules pour les URLs.
    /// Si <see cref="SecurityContext"/> est vide, il est construit depuis
    /// <see cref="CollaborativeSpace"/>.
    /// </summary>
    public void ApplyTenantDefaults()
    {
        var t = Tenant.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(t)) return;

        if (string.IsNullOrWhiteSpace(PlatformUrl))
            PlatformUrl = $"https://{t}-eu1-ifwe.3dexperience.3ds.com";

        if (string.IsNullOrWhiteSpace(ThreeDSpaceUrl))
            ThreeDSpaceUrl = $"https://{t}-eu1-space.3dexperience.3ds.com/enovia";

        if (string.IsNullOrWhiteSpace(FedSearchUrl))
            FedSearchUrl = $"https://{t}-eu1-fedsearch.3dexperience.3ds.com";

        if (string.IsNullOrWhiteSpace(PassportBaseUrl))
            PassportBaseUrl = $"https://{t}-eu1.iam.3dexperience.3ds.com";

        if (string.IsNullOrWhiteSpace(SecurityContext) &&
            !string.IsNullOrWhiteSpace(CollaborativeSpace))
        {
            SecurityContext =
                $"ctx::VPLMProjectLeader.Company Name.{CollaborativeSpace.Trim()}";
        }
    }
}
