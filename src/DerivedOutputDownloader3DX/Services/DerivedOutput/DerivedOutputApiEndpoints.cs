namespace DerivedOutputDownloader3DX.Services.DerivedOutput;

/// <summary>
/// URLs REST pour la famille dsdo (Derived Outputs Web Services).
/// Toutes les URLs 3DSpace incluent le paramètre <c>tenant</c> requis par la plateforme.
/// </summary>
public static class DerivedOutputApiEndpoints
{
    /// <summary>
    /// POST — Localiser les derived outputs d'une liste d'objets.
    /// Corps : { "referencedObject": [{ "source", "type", "id", "relativePath" }] }
    /// </summary>
    public static string LocateUrl(string threeDSpaceUrl, string tenant) =>
        $"{Base(threeDSpaceUrl)}/resources/v1/modeler/dsdo/dsdo:DerivedOutputs/Locate" +
        $"?tenant={Uri.EscapeDataString(tenant)}";

    /// <summary>
    /// POST — Obtenir un ticket de téléchargement FCS pour un fichier dérivé (nécessite CSRF).
    /// <paramref name="parentId"/> = id de l'entité DerivedOutputs.
    /// <paramref name="fileId"/>   = id de l'entrée DerivedOutputFiles.
    /// </summary>
    public static string DownloadTicketUrl(
        string threeDSpaceUrl,
        string parentId,
        string fileId,
        string tenant) =>
        $"{Base(threeDSpaceUrl)}/resources/v1/modeler/dsdo/dsdo:DerivedOutputs" +
        $"/{Uri.EscapeDataString(parentId)}/dsdo:DerivedOutputFiles/{Uri.EscapeDataString(fileId)}/DownloadTicket" +
        $"?tenant={Uri.EscapeDataString(tenant)}";

    /// <summary>
    /// GET — Profil utilisateur courant avec ses espaces collaboratifs.
    /// Permet de découvrir les SecurityContexts disponibles sans en connaître un à l'avance.
    /// </summary>
    public static string PnoPersonUrl(string threeDSpaceUrl, string tenant) =>
        $"{Base(threeDSpaceUrl)}/resources/modeler/pno/person" +
        $"?current=true&select=collabspaces&tenant={Uri.EscapeDataString(tenant)}";

    /// <summary>GET — Jeton CSRF (requis avant POST/PUT/DELETE sur 3DSpace).</summary>
    public static string CsrfUrl(string threeDSpaceUrl, string tenant) =>
        $"{Base(threeDSpaceUrl)}/resources/v1/application/CSRF" +
        $"?tenant={Uri.EscapeDataString(tenant)}";

    /// <summary>Surcharge sans tenant pour compatibilité <see cref="ThreeDSpaceCsrfClient"/>.</summary>
    public static string CsrfUrl(string threeDSpaceUrl) =>
        $"{Base(threeDSpaceUrl)}/resources/v1/application/CSRF";

    private static string Base(string url) => url.Trim().TrimEnd('/');
}
