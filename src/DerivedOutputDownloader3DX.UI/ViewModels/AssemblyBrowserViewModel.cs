using System.Collections.ObjectModel;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using DerivedOutputDownloader3DX.Services;
using DerivedOutputDownloader3DX.Services.Bom;
using DerivedOutputDownloader3DX.Services.Drawing;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.UI.ViewModels;

/// <summary>
/// ViewModel de la fenêtre de navigation d'assemblage.
/// Charge le BOM complet, les formats dérivés 3D et les mises en plan associées,
/// puis permet à l'utilisateur de sélectionner ce qu'il veut télécharger.
/// </summary>
public sealed class AssemblyBrowserViewModel : ViewModelBase
{
    private readonly BomExpander              _bom;
    private readonly DerivedOutputClient      _derived;
    private readonly DrawingFinder            _drawing;
    private readonly ILogger                  _log;

    private bool   _isLoading   = true;
    private string _loadingMsg  = "Chargement du BOM…";
    private int    _loadingPct  = 0;
    private int    _selectedCount;
    private int    _totalFormats;

    // ── Propriétés bindables ─────────────────────────────────────────────────

    public string AssemblyId    { get; }
    public string AssemblyTitle { get; }
    public string OutputDir     { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set { Set(ref _isLoading, value); DownloadCommand.RaiseCanExecuteChanged(); }
    }
    public string LoadingMessage { get => _loadingMsg;  private set => Set(ref _loadingMsg, value); }
    public int    LoadingPercent { get => _loadingPct;  private set => Set(ref _loadingPct, value); }
    public int    SelectedCount  { get => _selectedCount; private set { Set(ref _selectedCount, value); DownloadCommand.RaiseCanExecuteChanged(); } }
    public int    TotalFormats   { get => _totalFormats;  private set => Set(ref _totalFormats, value); }

    public ObservableCollection<DownloadRowVM>   Rows           { get; } = new();

    /// <summary>Filtres par format, peuplés après le chargement.</summary>
    public ObservableCollection<FormatFilterVM> FormatFilters  { get; } = new();

    // ── Commandes ────────────────────────────────────────────────────────────

    public RelayCommand SelectAllCommand    { get; }
    public RelayCommand DeselectAllCommand  { get; }
    public RelayCommand DownloadCommand     { get; }

    /// <summary>Bascule la sélection de tous les fichiers d'un format donné.
    /// CommandParameter = FormatFilterVM.</summary>
    public RelayCommand ToggleFormatCommand { get; }

    /// <summary>Levé quand l'utilisateur confirme le téléchargement.</summary>
    public event Action<IReadOnlyList<DownloadRowVM>>? DownloadRequested;

    // ── Construction ─────────────────────────────────────────────────────────

    public AssemblyBrowserViewModel(
        string assemblyId,
        string assemblyTitle,
        string outputDir,
        System.Net.Http.HttpClient http,
        ThreeExperienceOptions opts,
        ILoggerFactory logFactory)
    {
        AssemblyId    = assemblyId;
        AssemblyTitle = assemblyTitle;
        OutputDir     = outputDir;

        _log     = logFactory.CreateLogger<AssemblyBrowserViewModel>();
        _bom     = new BomExpander(http, opts, logFactory.CreateLogger<BomExpander>());
        _derived = new DerivedOutputClient(http, opts, logFactory.CreateLogger<DerivedOutputClient>());
        _drawing = new DrawingFinder(http, opts, logFactory.CreateLogger<DrawingFinder>());

        SelectAllCommand   = new RelayCommand(_ => SetAll(true),   _ => !IsLoading);
        DeselectAllCommand = new RelayCommand(_ => SetAll(false),  _ => !IsLoading);
        DownloadCommand    = new RelayCommand(_ => RequestDownload(),
            _ => !IsLoading && SelectedCount > 0);
        ToggleFormatCommand = new RelayCommand(param =>
        {
            if (param is not FormatFilterVM filter) return;

            var matching = Rows.Where(r => r.IsSelectable
                                        && r.Label.Equals(filter.FormatName,
                                               StringComparison.OrdinalIgnoreCase))
                               .ToList();

            // Si tous cochés → décocher ; sinon → cocher tous
            bool newValue = !matching.All(r => r.IsSelected);
            foreach (var r in matching)
                r.IsSelected = newValue;

            RefreshCount();
        }, _ => !IsLoading);
    }

    // ── Chargement ───────────────────────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading    = true;
        LoadingMessage = "Développement du BOM en cours…";
        LoadingPercent = 0;

        try
        {
            // 1. BOM complet (sous-assemblages + feuilles)
            var allNodes = (await _bom.ExpandAllNodesAsync(AssemblyId, ct)
                                      .ConfigureAwait(false)).ToList();

            var leaves = allNodes.Where(n => n.IsLeaf).ToList();
            LoadingMessage = $"BOM chargé : {allNodes.Count} nœud(s), {leaves.Count} pièce(s) feuille(s).";

            // 2. SecurityContext une seule fois
            var sc = await _derived.EnsureSecurityContextAsync(ct).ConfigureAwait(false);

            var formats = new List<DownloadRowVM>();
            int step    = 0;

            foreach (var node in allNodes)
            {
                ct.ThrowIfCancellationRequested();

                // En-tête assemblage ou pièce
                var kind = node.IsLeaf ? RowKind.PartHeader : RowKind.AssemblyHeader;
                var header = new DownloadRowVM
                {
                    Kind  = kind,
                    Level = node.Level - 1,
                    Label = node.Name
                };
                formats.Add(header);

                if (!node.IsLeaf) continue;

                step++;
                LoadingPercent = leaves.Count > 0 ? step * 100 / leaves.Count : 0;
                LoadingMessage = $"[{step}/{leaves.Count}] Analyse de « {Truncate(node.Name, 25)} »…";

                // 3a. Formats dérivés 3D de la pièce
                try
                {
                    var descs = await _derived.ListDerivedOutputsAsync(node.PhysicalId, ct)
                                              .ConfigureAwait(false);
                    foreach (var d in descs)
                    {
                        formats.Add(new DownloadRowVM
                        {
                            Kind       = RowKind.Format3D,
                            Level      = node.Level,
                            Label      = d.Format,
                            FileName   = d.FileName,
                            Category   = "3D",
                            Descriptor = d,
                            PartName   = node.Name,
                            PartPhysId = node.PhysicalId,
                            IsSelected = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[Browser] Derived outputs introuvables pour {Id}", node.PhysicalId);
                }

                // 3b. Mises en plan liées
                try
                {
                    var drawings = await _drawing.FindDrawingsForPartAsync(node.PhysicalId, sc, ct)
                                                 .ConfigureAwait(false);
                    foreach (var drw in drawings)
                    {
                        var drwDescs = await _derived.ListDerivedOutputsAsync(drw.PhysicalId, ct)
                                                     .ConfigureAwait(false);
                        foreach (var d in drwDescs)
                        {
                            formats.Add(new DownloadRowVM
                            {
                                Kind         = RowKind.FormatDRW,
                                Level        = node.Level,
                                Label        = d.Format,
                                FileName     = d.FileName,
                                Category     = "DRW",
                                DrawingTitle = drw.Title,
                                Descriptor   = d,
                                PartName     = node.Name,
                                PartPhysId   = node.PhysicalId,
                                IsSelected   = true
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[Browser] Drawings introuvables pour {Id}", node.PhysicalId);
                }
            }

            // 4. Calculer les filtres par format
            var uniqueFormats = formats
                .Where(r => r.IsSelectable)
                .GroupBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key)
                .Select(g => new FormatFilterVM
                {
                    FormatName = g.Key,
                    TotalCount = g.Count()
                })
                .ToList();

            // 5. Peupler les collections sur le thread UI
            App.Current.Dispatcher.Invoke(() =>
            {
                Rows.Clear();
                foreach (var r in formats)
                {
                    r.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(DownloadRowVM.IsSelected))
                            RefreshCount();
                    };
                    Rows.Add(r);
                }

                FormatFilters.Clear();
                foreach (var f in uniqueFormats)
                    FormatFilters.Add(f);

                RefreshCount();
            });

            LoadingMessage = $"Chargement terminé — {TotalFormats} format(s) disponible(s).";
        }
        catch (OperationCanceledException)
        {
            LoadingMessage = "Chargement annulé.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Browser] Erreur lors du chargement du BOM.");
            LoadingMessage = $"Erreur : {ex.Message}";
        }
        finally
        {
            IsLoading    = false;
            LoadingPercent = 100;
        }
    }

    // ── Sélection ────────────────────────────────────────────────────────────

    private void SetAll(bool value)
    {
        foreach (var r in Rows.Where(r => r.IsSelectable))
            r.IsSelected = value;
        RefreshCount();
    }

    private void RefreshCount()
    {
        SelectedCount = Rows.Count(r => r.IsSelectable && r.IsSelected);
        TotalFormats  = Rows.Count(r => r.IsSelectable);

        // Met à jour l'état visuel de chaque filtre de format
        foreach (var filter in FormatFilters)
            filter.Refresh(Rows);
    }

    private void RequestDownload()
    {
        var selected = Rows.Where(r => r.IsSelectable && r.IsSelected && r.Descriptor != null)
                           .ToList();
        if (selected.Count > 0)
            DownloadRequested?.Invoke(selected);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
