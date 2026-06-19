using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.Services;
using Microsoft.Extensions.Logging;

namespace DerivedOutputDownloader3DX.UI.ViewModels;

public sealed class LoginViewModel : ViewModelBase
{
    private readonly ThreeExperienceOptions _opts;
    private readonly ILoggerFactory _logFactory;

    private string _username = "";
    private string _errorMessage = "";
    private bool _isBusy;

    public string Username { get => _username; set => Set(ref _username, value); }
    public string ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public RelayCommand LoginCommand { get; }

    /// <summary>
    /// Levé après un login CAS réussi. Le paramètre est le client CAS prêt à l'emploi.
    /// </summary>
    public event Action<ThreeDxCasPassportClient>? LoginSucceeded;

    /// <summary>Fourni par le code-behind au moment du clic (PasswordBox non bindable).</summary>
    public Func<string>? GetPassword { get; set; }

    public LoginViewModel(ThreeExperienceOptions opts, ILoggerFactory logFactory)
    {
        _opts = opts;
        _logFactory = logFactory;
        LoginCommand = new RelayCommand(ExecuteLoginAsync, _ => !IsBusy);
    }

    private async Task ExecuteLoginAsync(object? _)
    {
        ErrorMessage = "";
        var password = GetPassword?.Invoke() ?? "";

        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "L'identifiant est requis.";
            return;
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "Le mot de passe est requis.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_opts.PassportBaseUrl))
        {
            ErrorMessage = "PassportBaseUrl absent dans appsettings.json.";
            return;
        }

        IsBusy = true;
        ThreeDxCasPassportClient? cas = null;
        try
        {
            cas = new ThreeDxCasPassportClient(
                _logFactory.CreateLogger<ThreeDxCasPassportClient>());
            await cas.LoginAsync(_opts, _opts.PassportBaseUrl, Username, password);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Échec de connexion : [{ex.GetType().Name}] {ex.Message}" +
                           (ex.InnerException != null ? $"\n→ {ex.InnerException.Message}" : "") +
                           $"\n{ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
            IsBusy = false;
            return;
        }

        // LoginAsync réussi — on lève l'événement en dehors du bloc catch CAS
        // pour que les erreurs d'ouverture de MainWindow restent distinctes.
        try
        {
            IsBusy = false;
            LoginSucceeded?.Invoke(cas);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Erreur ouverture fenêtre principale : [{ex.GetType().Name}] {ex.Message}" +
                           (ex.InnerException != null ? $"\n→ {ex.InnerException.Message}" : "") +
                           $"\n{ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
        }
    }
}
