using System.IO;
using System.Windows;
using DerivedOutputDownloader3DX.Services;
using DerivedOutputDownloader3DX.UI.Models;
using DerivedOutputDownloader3DX.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.UI.Views;

public partial class AssemblyBrowserWindow : Window
{
    private readonly AssemblyBrowserViewModel _vm;
    private readonly DerivedOutputClient       _derivedClient;

    public AssemblyBrowserWindow(
        AssemblyBrowserViewModel vm,
        DerivedOutputClient derivedClient)
    {
        InitializeComponent();
        _vm            = vm;
        _derivedClient = derivedClient;
        DataContext    = vm;
        vm.DownloadRequested += OnDownloadRequested;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Démarre le chargement du BOM dès que la fenêtre est affichée
        _ = _vm.LoadAsync();
    }

    private void OnDownloadRequested(IReadOnlyList<DownloadRowVM> selectedRows)
    {
        var outputDir = _vm.OutputDir;

        var progressVm = new DownloadProgressViewModel { OutputDirectory = outputDir };
        var jobItems   = selectedRows.Select(r => new DownloadJobItem
        {
            Format   = r.Category == "DRW" ? $"DRW/{r.Label}" : r.Label,
            FileName = $"[{SanitizeName(r.PartName)}] {r.FileName}"
        }).ToList();
        progressVm.AddJobs(jobItems);

        var progressWindow = new DownloadProgressWindow(progressVm) { Owner = this };
        progressWindow.Show();

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < selectedRows.Count; i++)
            {
                var row     = selectedRows[i];
                var jobItem = jobItems[i];
                progressVm.MarkInProgress(jobItem);

                try
                {
                    Directory.CreateDirectory(outputDir);

                    var path = await _derivedClient
                        .DownloadDerivedOutputAsync(row.Descriptor!, outputDir)
                        .ConfigureAwait(false);

                    progressVm.MarkDone(jobItem, path);
                }
                catch (Exception ex)
                {
                    progressVm.MarkFailed(jobItem, ex.Message);
                }
            }
        });
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean   = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(clean) ? "Part" : clean;
    }
}
