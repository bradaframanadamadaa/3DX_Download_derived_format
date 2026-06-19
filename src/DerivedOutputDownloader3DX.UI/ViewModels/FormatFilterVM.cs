namespace DerivedOutputDownloader3DX.UI.ViewModels;

/// <summary>
/// Représente un bouton de filtre "Cocher/décocher tout [FORMAT]"
/// dans la barre de filtres de l'AssemblyBrowserWindow.
/// </summary>
public sealed class FormatFilterVM : ViewModelBase
{
    private int _selectedCount;

    // ── Identité ─────────────────────────────────────────────────────────────

    /// <summary>Nom du format (ex. "PDF", "STEP_AP214", "ACIS").</summary>
    public string FormatName { get; init; } = "";

    /// <summary>Nombre total de fichiers de ce format dans la liste.</summary>
    public int TotalCount { get; init; }

    // ── État de sélection ────────────────────────────────────────────────────

    public int SelectedCount
    {
        get => _selectedCount;
        set
        {
            if (Set(ref _selectedCount, value))
            {
                Notify(nameof(StateIcon));
                Notify(nameof(StateTip));
                Notify(nameof(BorderColor));
            }
        }
    }

    /// <summary>0 = aucun sélectionné, 1 = partiel, 2 = tous sélectionnés.</summary>
    public int State => SelectedCount == 0 ? 0 : SelectedCount == TotalCount ? 2 : 1;

    /// <summary>Icône affichée sur le bouton selon l'état.</summary>
    public string StateIcon => State switch
    {
        2 => "✅",
        1 => "◑",
        _ => "☐"
    };

    /// <summary>Texte du bouton : icône + nom du format + compteur.</summary>
    public string ButtonLabel => $"{StateIcon}  {FormatName}";

    public string StateTip => State switch
    {
        2 => $"Tout désélectionner — {FormatName} ({TotalCount})",
        1 => $"Sélectionner tous — {FormatName} ({SelectedCount}/{TotalCount})",
        _ => $"Sélectionner tous — {FormatName} ({TotalCount})"
    };

    /// <summary>Couleur de bordure selon l'état.</summary>
    public string BorderColor => State switch
    {
        2 => "#005386",
        1 => "#7B4F9E",
        _ => "#BBBBBB"
    };

    /// <summary>Recalcule l'état à partir de la liste courante de rows.</summary>
    public void Refresh(IEnumerable<DownloadRowVM> rows)
    {
        SelectedCount = rows.Count(r => r.IsSelectable
                                     && r.Label.Equals(FormatName, StringComparison.OrdinalIgnoreCase)
                                     && r.IsSelected);
        // Notifie ButtonLabel qui dépend de StateIcon
        Notify(nameof(ButtonLabel));
    }
}
