using System.Collections.Specialized;
using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using ScriptScope = SqlVitals.Desktop.ViewModels.Pages.IndexHealthViewModel.ScriptScope;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Index Health, bound to <see cref="IndexHealthViewModel"/> (#150, #187): the code-behind fills the
/// details drawer with the missing index selected, tells each script what its grid selects and
/// shows, focuses a box whose value can't be used, and scrolls the index the verdict shows into view.
/// </summary>
public partial class IndexHealthPage : Page, IPageContext, IDetailsSource
{
    // ── The details drawer (#150): the selected missing index ─────────────────
    // Its figures, its columns and its CREATE statement with Copy, which the grid's last column cuts short.
    private const string SelectHint = "Select a missing index to see what it would serve, its columns and its CREATE statement.";

    private readonly RowDrawer _drawer = new("Missing index", SelectHint);

    public DrawerContent Details => _drawer.Content;

    public event Action<bool>? DetailsChanged
    {
        add    => _drawer.Changed += value;
        remove => _drawer.Changed -= value;
    }

    internal RowDetailsView? DetailsView => _drawer.View;

    private void ShowMissing(object row, bool open)
    {
        if (row is MissingIndex index)
            _drawer.Show($"{index.SchemaName}.{index.TableName}", RowDetails.Rows(index), open,
                         "CREATE statement (review before running it)", index.CreateIndexStatement);
    }

    internal IndexHealthViewModel ViewModel { get; }

    // Read once, and again on Refresh, from the connection's database (#140).
    public PageKind Kind => PageKind.Snapshot;
    public bool UsesDatabase => true;

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public IndexHealthPage(IndexHealthViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        DrawerParts.FollowSelection(MissingGrid, ShowMissing, _drawer.Clear);
        ViewModel.ShowRow += ShowTopMissing;
        ViewModel.InvalidField += FocusField;

        Follow(MissingGrid, MissingFilter, ViewModel.MissingScope);
        Follow(UnusedGrid, UnusedFilter, ViewModel.UnusedScope);
        Follow(FragGrid, FragFilter, ViewModel.FragmentationScope);
    }

    // What a script covers follows its grid: the rows selected, those shown, and whether a search filters them.
    private static void Follow(DataGrid grid, GridFilterBox filter, ScriptScope scope)
    {
        void Update() => scope.Update(grid.SelectedItems.Cast<object>().ToList(), grid.Items.Count, filter.IsActive);
        grid.SelectionChanged += (_, _) => Update();
        filter.FilterChanged += (_, _) => Update();
        ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) => Update();
        Update();
    }

    // The verdict's Show it: past a search that hides it, in view and in the drawer.
    private void ShowTopMissing(MissingIndex top)
    {
        if (!MissingGrid.Items.Contains(top))
            MissingFilter.Clear();
        MissingGrid.SelectedItem = top;
        MissingGrid.ScrollIntoView(top);
        ShowMissing(top, open: true);
    }

    // A value that can't be used: its box, ready to type over.
    private void FocusField(string field)
    {
        var box = field switch
        {
            IndexHealthViewModel.MinFragField    => TxtMinFragPct,
            IndexHealthViewModel.MinPagesField   => TxtMinPageCount,
            IndexHealthViewModel.ReorganizeField => TxtReorganizePct,
            _                                    => TxtRebuildPct,
        };
        box.Focus();
        box.SelectAll();
    }
}
