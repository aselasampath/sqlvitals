namespace SqlVitals.Desktop.Services;

/// <summary>
/// Awaiting the method doesn't throw: it reports its own errors (#169), so an <c>async void</c>
/// event handler may await it without a <c>try</c>/<c>catch</c>. <c>MvvmGuardTests</c> reads it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ReportsItsErrorsAttribute : Attribute;
