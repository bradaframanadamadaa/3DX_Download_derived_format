using System.Windows;
using System.Windows.Controls;

namespace DerivedOutputDownloader3DX.UI.ViewModels;

/// <summary>
/// Sélecteur de template pour les lignes de l'AssemblyBrowserWindow.
/// - FormatTemplate  → lignes de type Format3D / FormatDRW (avec checkbox)
/// - HeaderTemplate  → lignes de type AssemblyHeader / PartHeader (sans checkbox)
/// </summary>
public sealed class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FormatTemplate { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is DownloadRowVM row && row.IsSelectable
            ? FormatTemplate
            : HeaderTemplate;
}
