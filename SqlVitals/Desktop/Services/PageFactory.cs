using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>A page and its view model, as the page factory made them.</summary>
public sealed record PageInstance(Page View, PageViewModel ViewModel);

/// <summary>
/// How the container makes one page (#183): its tag in <see cref="PageCatalog"/>, its type, and
/// how to build the view and find its view model. <see cref="PageRegistrations"/> holds them all.
/// </summary>
public sealed record PageRegistration(string Tag, Type ViewType, Func<IServiceProvider, PageArgs, PageInstance> Create);

/// <summary>Makes pages with their view models (#183).</summary>
public interface IPageFactory
{
    /// <summary>
    /// The page for a tag in <see cref="PageCatalog"/>, its view model its <c>DataContext</c> and
    /// ready to run: the window calls <see cref="PageViewModel.OnNavigatedToAsync"/> once it shows
    /// the page, and <see cref="PageViewModel.OnNavigatedFrom"/> when it leaves it.
    /// </summary>
    /// <exception cref="ArgumentException">No page is registered for <paramref name="tag"/>.</exception>
    PageInstance Create(string tag, PageArgs args);

    /// <summary>The tags it can make.</summary>
    IReadOnlyCollection<string> Tags { get; }
}

/// <summary>The <see cref="IPageFactory"/> over the container's <see cref="PageRegistration"/>s.</summary>
public sealed class PageFactory(IServiceProvider services, IShellStatus shell, IRefreshScheduler scheduler) : IPageFactory
{
    private readonly Dictionary<string, PageRegistration> _pages =
        services.GetServices<PageRegistration>().ToDictionary(r => r.Tag, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Tags => _pages.Keys;

    public PageInstance Create(string tag, PageArgs args)
    {
        if (!_pages.TryGetValue(tag, out var registration))
            throw new ArgumentException($"No page is registered for \"{tag}\": add it to PageRegistrations.", nameof(tag));

        // Every query the page starts ends when the page is left (#167), from its view model or its code-behind.
        var lifetime = new PageLifetime();
        var page = registration.Create(services, args with { Repo = new LifetimeRepository(args.Repo, () => lifetime.Token) });
        // Until its wave of #180, the page's code-behind reads, as it did when the window called it.
        if (page is { View: ICodeBehindPage codeBehind, ViewModel: CodeBehindViewModel model })
            model.LoadThrough(codeBehind);
        page.ViewModel.Connect(tag, shell, scheduler, lifetime);
        page.View.DataContext = page.ViewModel;
        return page;
    }
}
