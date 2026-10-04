namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Tests that create a WPF presentation source: a real window off screen, or a fake source for key
/// presses. WPF keeps every source in one static list, which two threads adding and removing at
/// once can corrupt, so these run one class at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PresentationSourceCollection
{
    public const string Name = "Presentation sources";
}
