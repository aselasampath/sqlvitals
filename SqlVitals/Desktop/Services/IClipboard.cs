namespace SqlVitals.Desktop.Services;

/// <summary>
/// The clipboard (#182). The app's implementation retries while another program holds the
/// clipboard and says so when it can't (<see cref="Helpers.ClipboardHelper"/>).
/// </summary>
public interface IClipboard
{
    void SetText(string text);
}
