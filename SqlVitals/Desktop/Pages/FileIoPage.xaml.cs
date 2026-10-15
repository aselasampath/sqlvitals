using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// File I/O, bound to <see cref="FileIoViewModel"/> (#41, #187): the code-behind fills the details
/// drawer with the file selected, scrolls the file the verdict shows into view, and forwards the
/// top bar's Pause to the view model.
/// </summary>
public partial class FileIoPage : Page, IPageContext, IDetailsSource
{
    // Live: read every few seconds on the view model's timer (#169); the bar's Pause and Resume pause it (#140).
    public PageKind Kind => PageKind.Live;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
    public bool CanPause => true;
    public bool IsPaused => ViewModel.IsPaused;
    public void SetPaused(bool paused) => ViewModel.SetPaused(paused);

    // ── The details drawer (#150): the selected file ──────────────────────────
    private const string SelectHint = "Select a file to see why it's highlighted, its figures and where it is.";

    private readonly RowDrawer _drawer = new("File details", SelectHint);

    public DrawerContent Details => _drawer.Content;

    public event Action<bool>? DetailsChanged
    {
        add    => _drawer.Changed += value;
        remove => _drawer.Changed -= value;
    }

    internal RowDetailsView? DetailsView => _drawer.View;

    private void ShowFile(object row, bool open)
    {
        if (row is FileLatency file)
            _drawer.Show($"{file.DatabaseName} · {file.FileName}", RowDetails.Rows(file), open);
    }

    internal FileIoViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and Measuring's folds are remembered; none in some tests.</param>
    public FileIoPage(FileIoViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        DrawerParts.FollowSelection(FilesGrid, ShowFile, _drawer.Clear);
        ViewModel.ShowRow += ShowSlowest;
    }

    // The verdict's Show the slow files: the slowest past a search that hides it, in view and in the drawer.
    private void ShowSlowest(FileLatency file)
    {
        if (!FilesGrid.Items.Contains(file))
            FilesFilter.Clear();
        FilesGrid.SelectedItem = file;
        FilesGrid.ScrollIntoView(file);
        ShowFile(file, open: true);
    }
}
