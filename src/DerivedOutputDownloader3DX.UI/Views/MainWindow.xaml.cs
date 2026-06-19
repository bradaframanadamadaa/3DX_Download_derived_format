using System.IO;
using System.Net.Http;
using System.Windows;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Models;
using DerivedOutputDownloader3DX.Services;
using DerivedOutputDownloader3DX.UI.Models;
using DerivedOutputDownloader3DX.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel        _vm;
    private readonly ThreeExperienceOptions _opts;
    private readonly HttpClient           _http;
    private readonly ILoggerFactory       _logFactory;

    public MainWindow(ThreeExperienceOptions opts, ThreeDxCasPassportClient casClient, ILoggerFactory logFactory)
    {
        InitializeComponent();
        _opts       = opts;
        _http       = casClient.Http;
        _logFactory = logFactory;
        _vm = new MainViewModel(opts, casClient, logFactory);
        _vm.DownloadRequested      += OnDownloadRequested;
        _vm.AssemblyBrowseRequested += OnAssemblyBrowseRequested;
        DataContext = _vm;

        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                _vm.SearchCommand.Execute(null);
        };
    }

    private void OnDownloadRequested(IReadOnlyList<(DerivedOutputItem item, string outputDir)> jobs)
    {
        var progressVm = new DownloadProgressViewModel { OutputDirectory = _vm.OutputDirectory };
        var jobItems = jobs.Select(j => new DownloadJobItem
        {
            Format   = j.item.Format,
            FileName = j.item.FileName
        }).ToList();
        progressVm.AddJobs(jobItems);

        var progressWindow = new DownloadProgressWindow(progressVm) { Owner = this };
        progressWindow.Show();

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                var (item, outputDir) = jobs[i];
                var jobItem           = jobItems[i];
                progressVm.MarkInProgress(jobItem);
                try
                {
                    var path = await _vm.DerivedClient
                        .DownloadDerivedOutputAsync(item.Descriptor, outputDir)
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

    private void OnAssemblyBrowseRequested(string assemblyId, string assemblyTitle)
    {
        var browserVm = new AssemblyBrowserViewModel(
            assemblyId,
            assemblyTitle,
            _vm.OutputDirectory,
            _http,
            _opts,
            _logFactory);

        var browserWindow = new AssemblyBrowserWindow(browserVm, _vm.DerivedClient)
        {
            Owner = this
        };
        browserWindow.Show();
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean   = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(clean) ? "Part" : clean;
    }
}
