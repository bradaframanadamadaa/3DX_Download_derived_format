namespace DerivedOutputDownloader3DX.Configuration;

public sealed class ThreeExperienceOptions
{
    public const string SectionName = "ThreeExperience";

    public string PlatformUrl { get; set; } = "";
    public string ThreeDSpaceUrl { get; set; } = "";
    public string FedSearchUrl { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string SecurityContext { get; set; } = "";
    public string PassportBaseUrl { get; set; } = "";
    public string CasServiceUrl { get; set; } = "";
    public string PassportBaseUrlEnvironmentVariable { get; set; } = "THREE_DX_PASSPORT_URL";
    public string UsernameEnvironmentVariable { get; set; } = "THREE_DX_USERNAME";
    public string PasswordEnvironmentVariable { get; set; } = "THREE_DX_PASSWORD";
    public int SearchTop { get; set; } = 50;
    public string ConnectionProbeTitle { get; set; } = "titre_test";
    public string BearerTokenEnvironmentVariable { get; set; } = "THREE_DX_BEARER_TOKEN";
    public bool ForceCasAuthentication { get; set; }
}
