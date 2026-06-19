using System.Collections.ObjectModel;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using DerivedOutputDownloader3DX.Services;
using DerivedOutputDownloader3DX.UI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;

namespace DerivedOutputDownloader3DX.UI.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ThreeExperienceOptions _opts;
    private readonly FedSearchThreeDExperienceSearchService _searchService;
    private readonly DerivedOutputDownloader3DX.DerivedOutputClient _derivedClient;

    private string _searchTerm = "";
    private bool _searchByTitle = true;
    private bool _isBusy;
    private string _statusMessage = "Prêt.";
    private ThreeDxSearchCandidate? _selectedResult;
    private string _outputDirectory = "";
    private string? _selectedObjectTitle;

    // ── Propriétés bindables ──────────────────────────────────────────────────

    public string SearchTerm    { get => _searchTerm;    set => Set(ref _searchTerm, value); }
    public bool   SearchByTitle { get => _searchByTitle; set => Set(ref _searchByTitle, value); }
    public bool   SearchByName  { get => !_searchByTitle; set => SearchByTitle = !value; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            Set(ref _isBusy, value);
            SearchCommand?.RaiseCanExecuteChanged();
            DownloadCommand?.RaiseCanExecuteChanged();
            DownloadAssemblyCommand?.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            Set(ref _outputDirectory, value);
            DownloadCommand?.RaiseCanExecuteChanged();
            DownloadAssemblyCommand?.RaiseCanExecuteChanged();
        }
    }

    public string? SelectedObjectTitle { get => _selectedObjectTitle; set => Set(ref _selectedObjectTitle, value); }

    /// <summary>True si l'objet sélectionné est un assemblage VPMReference.</summary>
    public bool IsAssemblySelected =>
        _selectedResult?.Type is "VPMReference" or "Product" or "dseng:EngItem";

    public ObservableCollection<ThreeDxSearchCandidate> SearchResults    { get; } = new();
    public ObservableCollection<DerivedOutputItem>       DerivedOutputItems { get; } = new();

    public ThreeDxSearchCandidate? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (Set(ref _selectedResult, value))
            {
                Notify(nameof(IsAssemblySelected));
                DownloadAssemblyCommand?.RaiseCanExecuteChanged();
                if (value != null)
                    _ = LoadDerivedOutputsAsync(value.Id, value.Title);
            }
        }
    }

    public int SelectedFormatsCount => DerivedOutputItems.Count(i => i.IsSelected);

    // ── Commandes ─────────────────────────────────────────────────────────────

    public RelayCommand SearchCommand          { get; }
    public RelayCommand BrowseDirectoryCommand { get; }
    public RelayCommand DownloadCommand        { get; }
    public RelayCommand DownloadAssemblyCommand { get; }

    /// <summary>Levé quand l'utilisateur télécharge un objet unique.</summary>
    public event Action<IReadOnlyList<(DerivedOutputItem item, string outputDir)>>? DownloadRequested;

    /// <summary>Levé quand l'utilisateur ouvre le navigateur d'assemblage.</summary>
    public event Action<string, string>? AssemblyBrowseRequested;

    /// <summary>Expose le client de téléchargement pour la fenêtre principale.</summary>
    public DerivedOutputDownloader3DX.DerivedOutputClient DerivedClient => _derivedClient;

    // ── Construction ──────────────────────────────────────────────────────────

    public MainViewModel(
        ThreeExperienceOptions opts,
        ThreeDxCasPassportClient casClient,
        ILoggerFactory logFactory)
    {
        _opts = opts;

        _searchService = new FedSearchThreeDExperienceSearchService(
            opts,
            casClient.Http,
            disposeHttp: false,
            bearerToken: null,
            logFactory.CreateLogger<FedSearchThreeDExperienceSearchService>());

        _derivedClient = new DerivedOutputDownloader3DX.DerivedOutputClient(
            casClient.Http,
            opts,
            logFactory.CreateLogger<DerivedOutputDownloader3DX.DerivedOutputClient>());

        OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        SearchCommand = new RelayCommand(
            ExecuteSearchAsync,
            _ => !IsBusy);

        BrowseDirectoryCommand = new RelayCommand(
            _ => ExecuteBrowse(),
            _ => !IsBusy);

        DownloadCommand = new RelayCommand(
            ExecuteDownloadAsync,
            _ => !IsBusy && DerivedOutputItems.Any(i => i.IsSelected) && OutputDirectory.Length > 0);

        DownloadAssemblyCommand = new RelayCommand(
            ExecuteDownloadAssemblyAsync,
            _ => !IsBusy && IsAssemblySelected && OutputDirectory.Length > 0);
    }

    // ── Recherche ─────────────────────────────────────────────────────────────

    private async Task ExecuteSearchAsync(object? _)
    {
        if (string.IsNullOrWhiteSpace(SearchTerm))
        {
            StatusMessage = "Saisissez un terme de recherche.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Recherche en cours…";
        SearchResults.Clear();
        DerivedOutputItems.Clear();
        SelectedObjectTitle = null;

        Log.Information("[UI] Search — terme={Term} byTitle={ByTitle}", SearchTerm, SearchByTitle);
        try
        {
            var query = SearchByTitle
                ? new ThreeDxSearchQuery(
                    PlatformUrl:     _opts.PlatformUrl,
                    ThreeDSpaceUrl:  _opts.ThreeDSpaceUrl,
                    SecurityContext: _opts.SecurityContext,
                    Title:           SearchTerm.Trim())
                : new ThreeDxSearchQuery(
                    PlatformUrl:     _opts.PlatformUrl,
                    ThreeDSpaceUrl:  _opts.ThreeDSpaceUrl,
                    SecurityContext: _opts.SecurityContext,
                    Name:            SearchTerm.Trim());

            IReadOnlyList<ThreeDxSearchCandidate> results = SearchByTitle
                ? await _searchService.SearchByTitleAsync(query)
                : await _searchService.SearchByNameAsync(query);

            Log.Information("[UI] Search — {Count} résultat(s)", results.Count);
            foreach (var r in results) SearchResults.Add(r);

            StatusMessage = results.Count == 0
                ? "Aucun résultat."
                : $"{results.Count} résultat(s) trouvé(s).";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[UI] Search — erreur");
            StatusMessage = $"Erreur de recherche : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Formats dérivés (objet unique) ────────────────────────────────────────

    private async Task LoadDerivedOutputsAsync(string physicalId, string title)
    {
        IsBusy = true;
        StatusMessage = "Chargement des formats dérivés…";
        DerivedOutputItems.Clear();
        SelectedObjectTitle = title;
        Notify(nameof(SelectedFormatsCount));

        Log.Information("[UI] LoadDerivedOutputs — physicalId={Id}", physicalId);
        try
        {
            var descriptors = await _derivedClient.ListDerivedOutputsAsync(physicalId);

            foreach (var d in descriptors)
            {
                var item = new DerivedOutputItem(d, selected: descriptors.Count == 1);
                item.PropertyChanged += (_, _) =>
                {
                    Notify(nameof(SelectedFormatsCount));
                    DownloadCommand.RaiseCanExecuteChanged();
                };
                DerivedOutputItems.Add(item);
            }

            StatusMessage = descriptors.Count == 0
                ? "Aucun format dérivé disponible pour cet objet."
                : $"{descriptors.Count} format(s) dérivé(s) disponible(s).";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[UI] LoadDerivedOutputs — erreur physicalId={Id}", physicalId);
            StatusMessage = $"Erreur formats dérivés : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Téléchargement unitaire ───────────────────────────────────────────────

    private Task ExecuteDownloadAsync(object? _)
    {
        var selected = DerivedOutputItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(OutputDirectory))
            return Task.CompletedTask;

        DownloadRequested?.Invoke(selected.Select(item => (item, OutputDirectory)).ToList());
        return Task.CompletedTask;
    }

    // ── Mode assemblage ───────────────────────────────────────────────────────

    private Task ExecuteDownloadAssemblyAsync(object? _)
    {
        if (_selectedResult == null || string.IsNullOrWhiteSpace(OutputDirectory))
            return Task.CompletedTask;

        Log.Information("[UI] AssemblyBrowse — {Id} ({Title})",
            _selectedResult.Id, _selectedResult.Title);

        AssemblyBrowseRequested?.Invoke(_selectedResult.Id, _selectedResult.Title);
        return Task.CompletedTask;
    }

    // ── Sélection du dossier ──────────────────────────────────────────────────

    private void ExecuteBrowse()
    {
        var dlg = new OpenFolderDialog
        {
            Title            = "Sélectionner le dossier de destination",
            InitialDirectory = OutputDirectory
        };
        if (dlg.ShowDialog() == true)
            OutputDirectory = dlg.FolderName;
    }
}
