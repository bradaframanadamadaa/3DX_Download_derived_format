using System.Windows;
using DerivedOutputDownloader3DX.UI.ViewModels;

namespace DerivedOutputDownloader3DX.UI.Views;

public partial class DownloadProgressWindow : Window
{
    public DownloadProgressWindow(DownloadProgressViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
