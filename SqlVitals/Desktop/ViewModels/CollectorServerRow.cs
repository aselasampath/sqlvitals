using System.Windows.Input;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>A server in Settings → Collector service: who watches it, and the button that moves it (#172).</summary>
public sealed record CollectorServerRow(string Name, string Where, string ActionText, string ActionName, string? ActionToolTip,
                                        ICommand Act, bool CanAct);
