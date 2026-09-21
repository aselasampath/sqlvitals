using System.IO;
using System.Windows.Controls;

namespace SqlVitals.Installer.Views;

public partial class LicenseView : UserControl
{
    private const string ResourceName = "SqlVitals.LICENSE.txt";

    public LicenseView()
    {
        InitializeComponent();
        LicenseText.Text = ReadLicense();
    }

    /// <summary>The repository's LICENSE, embedded at build time (see SqlVitals.Installer.csproj).</summary>
    internal static string ReadLicense()
    {
        using var stream = typeof(LicenseView).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return "MIT License — the full text could not be loaded from this copy of Setup.";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
