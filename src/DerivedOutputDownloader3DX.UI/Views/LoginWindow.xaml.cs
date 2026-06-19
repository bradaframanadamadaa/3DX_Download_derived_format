using System.Windows;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Services;
using DerivedOutputDownloader3DX.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.UI.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow(ThreeExperienceOptions opts, ILoggerFactory logFactory)
    {
        InitializeComponent();
        _vm = new LoginViewModel(opts, logFactory);
        _vm.GetPassword = () => PasswordBox.Password;
        _vm.LoginSucceeded += OnLoginSucceeded;
        DataContext = _vm;

        UsernameBox.Focus();
        PasswordBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                _vm.LoginCommand.Execute(null);
        };
    }

    private void OnLoginSucceeded(ThreeDxCasPassportClient casClient)
    {
        // Déjà sur le thread UI (pas de ConfigureAwait dans LoginViewModel)
        var main = new MainWindow(App.Options, casClient, App.LogFactory);
        Application.Current.MainWindow = main;
        main.Show();
        Close();
    }
}
