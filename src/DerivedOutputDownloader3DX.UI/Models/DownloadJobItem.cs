using System.ComponentModel;

namespace DerivedOutputDownloader3DX.UI.Models;

public enum DownloadStatus { Pending, InProgress, Done, Failed }

/// <summary>Représente un téléchargement individuel dans la fenêtre de progression.</summary>
public sealed class DownloadJobItem : INotifyPropertyChanged
{
    private DownloadStatus _status = DownloadStatus.Pending;
    private string _statusText = "En attente…";
    private string? _outputPath;
    private string? _errorMessage;

    public string Format { get; init; } = "";
    public string FileName { get; init; } = "";

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            StatusText = value switch
            {
                DownloadStatus.Pending    => "En attente…",
                DownloadStatus.InProgress => "Téléchargement…",
                DownloadStatus.Done       => "✅ Terminé",
                DownloadStatus.Failed     => $"❌ Erreur",
                _                         => ""
            };
            Notify(nameof(Status));
            Notify(nameof(StatusIcon));
        }
    }

    public string StatusIcon => _status switch
    {
        DownloadStatus.Pending    => "⏳",
        DownloadStatus.InProgress => "🔄",
        DownloadStatus.Done       => "✅",
        DownloadStatus.Failed     => "❌",
        _                         => ""
    };

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; Notify(nameof(StatusText)); }
    }

    public string? OutputPath
    {
        get => _outputPath;
        set { _outputPath = value; Notify(nameof(OutputPath)); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            _errorMessage = value;
            if (value != null)
                StatusText = $"❌ {value}";
            Notify(nameof(ErrorMessage));
        }
    }

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
