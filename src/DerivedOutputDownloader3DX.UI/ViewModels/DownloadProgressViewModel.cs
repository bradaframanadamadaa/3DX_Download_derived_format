using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using DerivedOutputDownloader3DX.UI.Models;

namespace DerivedOutputDownloader3DX.UI.ViewModels;

public sealed class DownloadProgressViewModel : ViewModelBase
{
    private int _doneCount;
    private bool _isCompleted;
    private bool _hasErrors;
    private string _outputDirectory = "";

    public ObservableCollection<DownloadJobItem> Jobs { get; } = new();

    public int TotalCount => Jobs.Count;
    public int DoneCount { get => _doneCount; private set { Set(ref _doneCount, value); Notify(nameof(ProgressText)); } }
    public bool IsCompleted { get => _isCompleted; private set => Set(ref _isCompleted, value); }
    public bool HasErrors { get => _hasErrors; private set => Set(ref _hasErrors, value); }
    public string ProgressText => $"{DoneCount} / {TotalCount} fichier(s)";
    public string OutputDirectory { get => _outputDirectory; set => Set(ref _outputDirectory, value); }

    public RelayCommand OpenFolderCommand { get; }

    public DownloadProgressViewModel()
    {
        OpenFolderCommand = new RelayCommand(_ =>
        {
            if (Directory.Exists(OutputDirectory))
                Process.Start("explorer.exe", OutputDirectory);
        });
    }

    public void AddJobs(IEnumerable<DownloadJobItem> jobs)
    {
        foreach (var j in jobs) Jobs.Add(j);
        Notify(nameof(TotalCount));
        Notify(nameof(ProgressText));
    }

    public void MarkInProgress(DownloadJobItem job) =>
        App.Current.Dispatcher.Invoke(() => job.Status = DownloadStatus.InProgress);

    public void MarkDone(DownloadJobItem job, string path)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            job.Status = DownloadStatus.Done;
            job.OutputPath = path;
            DoneCount++;
            CheckCompleted();
        });
    }

    public void MarkFailed(DownloadJobItem job, string error)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            job.Status = DownloadStatus.Failed;
            job.ErrorMessage = error;
            HasErrors = true;
            DoneCount++;
            CheckCompleted();
        });
    }

    private void CheckCompleted()
    {
        if (DoneCount >= TotalCount)
            IsCompleted = true;
    }
}
