using System.Windows.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Every page declares whether it is live, a snapshot or history (#140): a page added to the
/// sidebar, or a new page class, fails here until it implements <see cref="IPageContext"/>.
/// </summary>
public sealed class PageCatalogTests
{
    [Fact]
    public void EveryPageInTheMenu_HasAPageType() =>
        Assert.All(NavMenu.AllPages.Select(p => p.Tag).Append("ServerDetail"),
                   tag => Assert.True(PageCatalog.Types.ContainsKey(tag), $"\"{tag}\" is not in PageCatalog."));

    [Fact]
    public void EveryPage_DeclaresItsKind() =>
        Assert.All(PageCatalog.Types, entry =>
            Assert.True(typeof(IPageContext).IsAssignableFrom(entry.Value),
                        $"{entry.Value.Name} (\"{entry.Key}\") doesn't implement IPageContext."));

    [Fact]
    public void EveryPageClass_IsInTheCatalog()
    {
        var pages = typeof(MainWindow).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, Namespace: "SqlVitals.Desktop.Pages" } && typeof(Page).IsAssignableFrom(t));

        Assert.All(pages, type => Assert.Contains(type, PageCatalog.Types.Values));
    }
}
