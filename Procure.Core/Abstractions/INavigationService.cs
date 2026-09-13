using System.Threading.Tasks;

namespace Procure.Abstractions;

public enum AppRoute
{
    Dashboard,
    Board,
    Tasks,
    Notes,
    CallOff,
    Settings,
}

/// <summary>
/// Top-level navigation between the app's tabs. Replaces the handful of
/// <c>Shell.Current.GoToAsync("//route")</c> calls in the view models.
/// </summary>
public interface INavigationService
{
    Task GoToAsync(AppRoute route);

    /// <summary>Board with the "create a PR now" action queued (was <c>//prboard?action=new</c>).</summary>
    Task GoToBoardAndCreateAsync();

    /// <summary>Go to the board and drop <paramref name="search"/> into its search box
    /// (how Tasks / Notes "open linked PRs" works).</summary>
    Task GoToBoardWithSearchAsync(string search);
}
