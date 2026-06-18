namespace DerivedOutputDownloader3DX.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "App";

    public bool DryRun { get; set; }
    public bool UseMockSearch { get; set; }
    public string OutputDirectory { get; set; } = "";
}
