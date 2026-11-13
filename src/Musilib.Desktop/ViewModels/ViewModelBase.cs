using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Musilib.Application.Errors;
using Musilib.Desktop.Services;

namespace Musilib.Desktop.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;
}

/// <summary>Écran de l'application : cycle de navigation et gestion homogène des erreurs.</summary>
public abstract partial class PageViewModel : ViewModelBase
{
    protected PageViewModel(NotificationService notifications, ILogger logger)
    {
        Notifications = notifications;
        Logger = logger;
    }

    protected NotificationService Notifications { get; }
    protected ILogger Logger { get; }

    public abstract string Title { get; }

    /// <summary>Vrai si l'écran porte sur la playlist courante (affiche le sélecteur de playlist).</summary>
    public virtual bool UsesPlaylist => false;

    public virtual Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;
    public virtual void OnNavigatedFrom() { }
    /// <summary>Appelé lorsque la playlist courante change pendant que l'écran est affiché.</summary>
    public virtual Task OnPlaylistChangedAsync() => Task.CompletedTask;

    /// <summary>Exécute une action en affichant l'état occupé et en convertissant les erreurs en notifications.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? busyText = null, string? successTitle = null, string? successMessage = null)
    {
        IsBusy = true;
        BusyText = busyText;
        try
        {
            await action();
            if (successTitle is not null) Notifications.Success(successTitle, successMessage);
            return true;
        }
        catch (OperationCanceledException)
        {
            Notifications.Show("Opération annulée");
        }
        catch (AuthenticationRequiredException ex)
        {
            Notifications.Warning($"Connexion {ex.Provider} requise", ex.Message);
        }
        catch (ConfigurationMissingException ex)
        {
            Notifications.Warning("Configuration incomplète", ex.Message);
        }
        catch (QuotaExceededException ex)
        {
            Notifications.Warning($"Quota {ex.Provider} atteint", ex.Message);
        }
        catch (ApiException ex)
        {
            Logger.LogWarning("Erreur API {Provider} : {Message}", ex.Provider, ex.Message);
            Notifications.Error($"Erreur {ex.Provider}", ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or HttpRequestException or PlatformNotSupportedException)
        {
            Logger.LogWarning(ex, "Opération échouée");
            Notifications.Error("Opération impossible", ex.Message);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Erreur inattendue");
            Notifications.Error("Erreur inattendue", ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
        return false;
    }
}
