using DerivedOutputDownloader3DX.Models;
using System.Windows;

namespace DerivedOutputDownloader3DX.UI.ViewModels;

/// <summary>Type de ligne dans la liste de l'AssemblyBrowserWindow.</summary>
public enum RowKind
{
    /// <summary>En-tête sous-assemblage (pas de case à cocher).</summary>
    AssemblyHeader,
    /// <summary>En-tête pièce feuille (pas de case à cocher).</summary>
    PartHeader,
    /// <summary>Format dérivé 3D (STEP, ACIS…) de la pièce.</summary>
    Format3D,
    /// <summary>Format dérivé d'une mise en plan (PDF…) liée à la pièce.</summary>
    FormatDRW
}

/// <summary>
/// Représente une ligne dans l'arborescence de téléchargement d'assemblage.
/// Les lignes de type <see cref="RowKind.Format3D"/> et <see cref="RowKind.FormatDRW"/>
/// sont cochables et portent un <see cref="DerivedOutputDescriptor"/> pour le téléchargement.
/// </summary>
public sealed class DownloadRowVM : ViewModelBase
{
    private bool _isSelected;

    // ── Identité ─────────────────────────────────────────────────────────────

    public RowKind Kind       { get; init; }
    public int     Level      { get; init; }

    /// <summary>Texte principal : nom de la pièce / sous-assemblage / format.</summary>
    public string  Label      { get; init; } = "";

    /// <summary>Nom du fichier (Format3D / FormatDRW uniquement).</summary>
    public string  FileName   { get; init; } = "";

    /// <summary>"3D" ou "DRW" affiché comme badge coloré sur les lignes de format.</summary>
    public string  Category   { get; init; } = "";

    /// <summary>Titre de la mise en plan d'origine (FormatDRW uniquement).</summary>
    public string  DrawingTitle { get; init; } = "";

    // ── Téléchargement ───────────────────────────────────────────────────────

    /// <summary>Descripteur nécessaire pour appeler DownloadDerivedOutputAsync.</summary>
    public DerivedOutputDescriptor? Descriptor { get; init; }

    /// <summary>Nom de la pièce propriétaire (pour le sous-répertoire de sortie).</summary>
    public string PartName   { get; init; } = "";

    /// <summary>physicalId de la pièce propriétaire.</summary>
    public string PartPhysId { get; init; } = "";

    // ── Sélection ────────────────────────────────────────────────────────────

    public bool IsSelectable => Kind is RowKind.Format3D or RowKind.FormatDRW;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    // ── Helpers de mise en page ───────────────────────────────────────────────

    /// <summary>Marge gauche calculée d'après le niveau (pour l'indentation visuelle).</summary>
    public Thickness IndentMargin => new(Level * 18 + (IsSelectable ? 0 : 4), 2, 4, 2);

    public bool IsHeader   => Kind is RowKind.AssemblyHeader or RowKind.PartHeader;
    public bool IsAssembly => Kind == RowKind.AssemblyHeader;
    public bool IsFormat   => IsSelectable;

    /// <summary>Texte complet affiché : Label + FileName si disponible.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(FileName)
        ? Label
        : $"{Label} — {FileName}";

    /// <summary>Couleur de fond alternée pour distinguer les blocs.</summary>
    public string CategoryBackground => Category switch
    {
        "3D"  => "#1E6BA3",
        "DRW" => "#7B4F9E",
        _     => "Transparent"
    };
}
