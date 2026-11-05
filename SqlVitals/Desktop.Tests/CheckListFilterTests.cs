using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The multi-select filter box (#237) in an off-screen window: closed it shows what's ticked; open,
/// its check boxes tick the values, "(All)" clears them, and refilling the values leaves the list
/// open with its ticks. The open list is rendered to %TEMP%\SqlVitalsTests\page-template-rollout.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class CheckListFilterTests
{
    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static void Shown(MultiFilterChoices box, Action<CheckListFilter> test)
    {
        var filter = new CheckListFilter { DataContext = box, Width = 150 };
        var window = new Window { Content = new StackPanel { Children = { filter } }, Width = 300, Height = 200, Left = -20_000, Top = -20_000,
                                  WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            test(filter);
        }
        finally
        {
            filter.IsOpen = false;
            window.Close();
        }
    }

    private static void Open(CheckListFilter filter)
    {
        filter.IsOpen = true;
        ((FrameworkElement)filter.DropList.Child).UpdateLayout();
    }

    // The value check boxes in the open list, in order.
    private static List<CheckBox> Boxes(CheckListFilter filter)
    {
        ((FrameworkElement)filter.DropList.Child).UpdateLayout();
        return Enumerable.Range(0, filter.ChoiceList.Items.Count)
            .Select(i => (CheckBox)VisualTreeHelper.GetChild(filter.ChoiceList.ItemContainerGenerator.ContainerFromIndex(i), 0))
            .ToList();
    }

    private static string Summary(CheckListFilter filter) =>
        ((TextBlock)filter.BtnOpen.Template.FindName("TxtSummary", filter.BtnOpen)).Text;

    [Theory, MemberData(nameof(Themes))]
    public void TickingInTheList_FiltersAndSaysWhatsTicked_AndAllClearsIt(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var box = new MultiFilterChoices();
        box.Replace(["OrderService", "BillingService", "Reporting"]);

        Shown(box, filter =>
        {
            Assert.Equal("(All)", Summary(filter));
            Assert.Equal("(All)", filter.BtnOpen.ToolTip);
            Open(filter);
            Assert.True(filter.ChkAll.IsChecked);
            Assert.Equal(Visibility.Collapsed, filter.TxtNoValues.Visibility);
            var boxes = Boxes(filter);
            Assert.Equal(["BillingService", "OrderService", "Reporting"], boxes.Select(b => ((TextBlock)b.Content).Text));

            TemplateRolloutTests.Click(boxes[1]);
            Assert.Equal("OrderService", Summary(filter));
            Assert.False(filter.ChkAll.IsChecked);
            TemplateRolloutTests.Click(boxes[0]);
            Assert.Equal("2 selected", Summary(filter));
            Assert.Equal($"BillingService{Environment.NewLine}OrderService", filter.BtnOpen.ToolTip);
            Assert.Equal(["BillingService", "OrderService"], box.Values!.Order());
            TemplateRolloutTests.Render((FrameworkElement)filter.DropList.Child, $"check-list-filter-{theme}.png");

            // "(All)" clears the ticks; clicked again it stays ticked: there's nothing to clear.
            TemplateRolloutTests.Click(filter.ChkAll);
            Assert.True(filter.ChkAll.IsChecked);
            Assert.All(boxes, b => Assert.False(b.IsChecked));
            Assert.Equal("(All)", Summary(filter));
            TemplateRolloutTests.Click(filter.ChkAll);
            Assert.True(filter.ChkAll.IsChecked);
            Assert.True(box.IsAll);
            Assert.True(filter.IsOpen);
        });
        errors.AssertNone();
    });

    [Fact]
    public void Refilled_WhileOpen_TheListStaysOpen_WithItsTicks() => UiThread.Run(() =>
    {
        var box = new MultiFilterChoices();
        box.Replace(["OrderService", "BillingService"]);

        Shown(box, filter =>
        {
            Open(filter);
            TemplateRolloutTests.Click(Boxes(filter)[0]);   // BillingService
            var ticked = Boxes(filter)[0];

            // The calls roll over: BillingService's are gone, others arrive.
            box.Replace(["OrderService", "Archive"]);

            Assert.True(filter.IsOpen);
            var boxes = Boxes(filter);
            Assert.Equal(["Archive", "BillingService", "OrderService"], boxes.Select(b => ((TextBlock)b.Content).Text));
            Assert.Same(ticked, boxes[1]);   // the same check box, moved: not built again
            Assert.Equal([false, true, false], boxes.Select(b => b.IsChecked == true));
            Assert.Equal("BillingService", Summary(filter));
        });
    });

    [Fact]
    public void ClosedByAClickElsewhere_TheKeyboardGoesBackToTheBox() => UiThread.Run(() =>
    {
        var box = new MultiFilterChoices();
        box.Replace(["OrderService"]);

        Shown(box, filter =>
        {
            Window.GetWindow(filter)!.Activate();
            Open(filter);
            Assert.True(filter.ChkAll.Focus());
            Assert.True(filter.DropList.Child.IsKeyboardFocusWithin);

            filter.IsOpen = false;   // as a click outside closes it
            UiThread.Wait(Task.Delay(50));   // Closed is raised once the popup's window has gone

            Assert.True(filter.BtnOpen.IsKeyboardFocused);
        });
    });

    [Fact]
    public void ManyValues_OnlyThoseInViewGetACheckBox() => UiThread.Run(() =>
    {
        var box = new MultiFilterChoices();
        box.Replace(Enumerable.Range(0, 500).Select(i => $"HOST-{i:000}"));

        Shown(box, filter =>
        {
            Open(filter);
            var built = Enumerable.Range(0, 500).Count(i => filter.ChoiceList.ItemContainerGenerator.ContainerFromIndex(i) is not null);
            Assert.InRange(built, 1, 60);
        });
    });

    [Fact]
    public void NoValuesYet_TheListSaysSo() => UiThread.Run(() =>
    {
        Shown(new MultiFilterChoices(), filter =>
        {
            Open(filter);
            Assert.Equal(Visibility.Visible, filter.TxtNoValues.Visibility);
            Assert.True(filter.ChkAll.IsChecked);
        });
    });
}
