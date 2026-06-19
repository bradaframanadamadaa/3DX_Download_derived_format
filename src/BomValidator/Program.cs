/*
 * BomValidator — programme de validation du bloc fonctionnel BOM + Derived Outputs + Mises en plan
 *
 * Utilisation :
 *   dotnet run --project src\BomValidator -- --title "Cardan_Manivelle"
 *   dotnet run --project src\BomValidator -- --id    "F763D8FB05A1160066FE507C00000EFD"
 *
 * Le programme :
 *   1. Lit appsettings.json
 *   2. Demande login + mot de passe en interactif
 *   3. Cherche l'assemblage (FedSearch)
 *   4. Développe le BOM récursivement
 *   5. Pour chaque pièce feuille :
 *        a. Liste les formats dérivés de la pièce (STEP, ACIS…)
 *        b. Cherche les mises en plan (Drawing) liées à la pièce
 *        c. Pour chaque Drawing trouvé, liste ses formats dérivés (PDF…)
 *   6. Affiche un tableau récapitulatif — aucun téléchargement
 */

using DerivedOutputDownloader3DX;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using DerivedOutputDownloader3DX.Services;
using DerivedOutputDownloader3DX.Services.Bom;
using DerivedOutputDownloader3DX.Services.Drawing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

// ── Config ────────────────────────────────────────────────────────────────────

var configPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory,
                 "..", "..", "..", "..", "DerivedOutputDownloader3DX", "appsettings.json"));

if (!File.Exists(configPath))
    configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"[ERREUR] appsettings.json introuvable (cherché : {configPath})");
    return 1;
}

var config = new ConfigurationBuilder()
    .SetBasePath(Path.GetDirectoryName(configPath)!)
    .AddJsonFile(Path.GetFileName(configPath), optional: false)
    .Build();

var opts = config.GetSection(ThreeExperienceOptions.SectionName)
                 .Get<ThreeExperienceOptions>()
           ?? new ThreeExperienceOptions();

// ── Args ──────────────────────────────────────────────────────────────────────

string? searchTitle = null;
string? searchId    = null;

for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--title") searchTitle = args[i + 1];
    if (args[i] == "--id")    searchId    = args[i + 1];
}

if (searchTitle == null && searchId == null)
{
    Console.Write("Titre ou ID de l'assemblage à analyser : ");
    var input = Console.ReadLine()?.Trim() ?? "";
    if (input.Length >= 28 && input.All(c => "0123456789ABCDEFabcdef".Contains(c)))
        searchId = input;
    else
        searchTitle = input;
}

// ── Logging ───────────────────────────────────────────────────────────────────
// On active Debug pour voir les réponses brutes de DrawingFinder
using var logFactory = LoggerFactory.Create(b =>
    b.AddConsole(o => { o.FormatterName = "simple"; })
     .AddFilter("DerivedOutputDownloader3DX.Services.Drawing", LogLevel.Information) // URLs + réponses brutes
     .AddFilter("DerivedOutputDownloader3DX", LogLevel.Warning));

// ── Login ─────────────────────────────────────────────────────────────────────

Console.Write("Identifiant 3DEXPERIENCE : ");
var username = Console.ReadLine()?.Trim() ?? "";
Console.Write("Mot de passe             : ");
var password = ReadPassword();
Console.WriteLine();

Print("Connexion en cours…");

var cas = new ThreeDxCasPassportClient(
    logFactory.CreateLogger<ThreeDxCasPassportClient>());

try
{
    await cas.LoginAsync(opts, opts.PassportBaseUrl, username, password);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ERREUR] Login échoué : {ex.Message}");
    return 2;
}
PrintOk("Connexion établie.");

// ── Recherche de l'assemblage racine ─────────────────────────────────────────

string assemblyId;
string assemblyTitle;

if (!string.IsNullOrWhiteSpace(searchId))
{
    assemblyId    = searchId!;
    assemblyTitle = searchId!;
    PrintOk($"Mode ID direct : {assemblyId}");
}
else
{
    Print($"Recherche FedSearch : titre = \"{searchTitle}\"…");
    var searchSvc = new FedSearchThreeDExperienceSearchService(
        opts,
        cas.Http,
        disposeHttp: false,
        bearerToken: null,
        logFactory.CreateLogger<FedSearchThreeDExperienceSearchService>());

    var query = new ThreeDxSearchQuery(
        PlatformUrl:     opts.PlatformUrl,
        ThreeDSpaceUrl:  opts.ThreeDSpaceUrl,
        SecurityContext: opts.SecurityContext,
        Title:           searchTitle!);

    var results = await searchSvc.SearchByTitleAsync(query);

    if (results.Count == 0)
    {
        Console.Error.WriteLine("[ERREUR] Aucun résultat pour ce titre.");
        return 3;
    }

    if (results.Count == 1)
    {
        assemblyId    = results[0].Id;
        assemblyTitle = results[0].Title;
        PrintOk($"1 résultat : [{results[0].Type}] {assemblyTitle}  ({assemblyId})");
    }
    else
    {
        Console.WriteLine($"\n  {results.Count} résultats — choisissez :");
        for (var i = 0; i < results.Count; i++)
            Console.WriteLine($"  [{i + 1}] {results[i].Title}  |  {results[i].Type}  |  {results[i].Name}");
        Console.Write("  Numéro : ");
        if (!int.TryParse(Console.ReadLine(), out var choice) || choice < 1 || choice > results.Count)
        {
            Console.Error.WriteLine("[ERREUR] Choix invalide.");
            return 3;
        }
        assemblyId    = results[choice - 1].Id;
        assemblyTitle = results[choice - 1].Title;
    }
}

// ── BOM ───────────────────────────────────────────────────────────────────────

Console.WriteLine();
Print($"Développement du BOM de « {assemblyTitle} »…");

var bom = new BomExpander(
    cas.Http, opts, logFactory.CreateLogger<BomExpander>());

List<BomNode> allNodes;
try
{
    var r = await bom.ExpandAllNodesAsync(assemblyId);
    allNodes = r.ToList();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ERREUR] BOM expand : {ex.Message}");
    return 4;
}

var leaves      = allNodes.Where(n => n.IsLeaf).ToList();
var assemblies  = allNodes.Where(n => !n.IsLeaf).ToList();
PrintOk($"BOM développé : {allNodes.Count} nœud(s) total ({assemblies.Count} sous-assemblage(s), {leaves.Count} pièce(s) feuille(s)).");

if (allNodes.Count == 0)
{
    Console.WriteLine("  → Aucun nœud trouvé (assemblage vide ou erreur de type).");
    return 0;
}

// ── Derived Outputs + Mises en plan (feuilles uniquement) ─────────────────────

Console.WriteLine();
Print($"Récupération des formats dérivés et mises en plan pour {leaves.Count} pièce(s) feuille(s)…");

var derivedClient = new DerivedOutputClient(
    cas.Http, opts, logFactory.CreateLogger<DerivedOutputClient>());
var drawingFinder = new DrawingFinder(
    cas.Http, opts, logFactory.CreateLogger<DrawingFinder>());

// Résolution du SecurityContext une seule fois
var sc = await derivedClient.EnsureSecurityContextAsync();

// Dictionnaire physicalId → PartRow (pour les feuilles uniquement)
var leafRows = new Dictionary<string, PartRow>(StringComparer.OrdinalIgnoreCase);

for (var i = 0; i < leaves.Count; i++)
{
    var node = leaves[i];
    Console.Write($"\r  [{i + 1}/{leaves.Count}] {Truncate(node.Name, 30),-32}");

    // a. Formats dérivés de la pièce
    string partFormats;
    try
    {
        var desc = await derivedClient.ListDerivedOutputsAsync(node.PhysicalId);
        partFormats = desc.Count == 0
            ? "(aucun)"
            : string.Join("  ", desc.Select(d => $"{d.Format}:{d.FileName}"));
    }
    catch
    {
        partFormats = "(erreur dsdo)";
    }

    // b. Mises en plan liées
    var drawingRows = new List<DrawingRow>();
    try
    {
        var drawings = await drawingFinder.FindDrawingsForPartAsync(node.PhysicalId, sc);
        foreach (var drw in drawings)
        {
            string drwFormats;
            try
            {
                var drwDesc = await derivedClient.ListDerivedOutputsAsync(drw.PhysicalId);
                drwFormats = drwDesc.Count == 0
                    ? "(aucun)"
                    : string.Join("  ", drwDesc.Select(d => $"{d.Format}:{d.FileName}"));
            }
            catch { drwFormats = "(erreur dsdo)"; }
            drawingRows.Add(new DrawingRow(drw.Title, drw.PhysicalId, drwFormats));
        }
    }
    catch (Exception ex)
    {
        drawingRows.Add(new DrawingRow("(erreur recherche)", "", ex.Message));
    }

    leafRows[node.PhysicalId] = new PartRow(node.Name, node.PhysicalId, node.Level, partFormats, drawingRows);
}

Console.WriteLine("\r" + new string(' ', 60) + "\r");

// ── Affichage — structure complète ────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine(new string('═', 115));
Console.WriteLine($"  NOMENCLATURE — {assemblyTitle}");
Console.WriteLine(new string('═', 115));

foreach (var node in allNodes)
{
    var indent  = new string(' ', (node.Level - 1) * 2);
    var nameCol = $"{indent}{Truncate(node.Name, 35 - (node.Level - 1) * 2)}";
    var marker  = node.IsLeaf ? "●" : "▶";

    Console.WriteLine($"  {marker} Niv{node.Level,-2}  {nameCol,-36}  {node.PhysicalId}");

    if (node.IsLeaf && leafRows.TryGetValue(node.PhysicalId, out var row))
    {
        var pad = new string(' ', 10 + (node.Level - 1) * 2);
        Console.WriteLine($"{pad}[3D]   {row.Formats}");

        if (row.Drawings.Count == 0)
            Console.WriteLine($"{pad}[DRW]  (aucune mise en plan trouvée)");
        else
            foreach (var drw in row.Drawings)
                Console.WriteLine($"{pad}[DRW]  {Truncate(drw.Title, 28),-30}  {drw.PhysId,-36}  {drw.Formats}");
    }

    Console.WriteLine(new string('─', 115));
}

var totalParts    = leaves.Count;
var totalDrawings = leafRows.Values.Sum(r => r.Drawings.Count);
Console.WriteLine($"  {assemblies.Count} sous-assemblage(s)  |  {totalParts} pièce(s) feuille(s)  |  {totalDrawings} mise(s) en plan trouvée(s)");
Console.WriteLine(new string('═', 115));
Console.WriteLine();

return 0;

// ── Helpers (local functions — avant les déclarations de types) ───────────────

static void Print(string msg)   => Console.WriteLine($"  …  {msg}");
static void PrintOk(string msg) => Console.WriteLine($"  ✓  {msg}");

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 1)] + "…";

static string ReadPassword()
{
    var pwd = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace && pwd.Length > 0)
        {
            pwd.Remove(pwd.Length - 1, 1);
            Console.Write("\b \b");
            continue;
        }
        if (key.KeyChar != '\0') { pwd.Append(key.KeyChar); Console.Write('*'); }
    }
    return pwd.ToString();
}

// ── Records (déclarations de types — après les fonctions locales) ─────────────

record PartRow(string Name, string PhysId, int Level, string Formats, List<DrawingRow> Drawings);
record DrawingRow(string Title, string PhysId, string Formats);
