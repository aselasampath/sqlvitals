using System;
using System.IO;

namespace SqlVitals.Installer.Core;

/// <summary>Turns exceptions into a plain-language problem plus what the user can do about it.</summary>
public static class ErrorMessages
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation    = unchecked((int)0x80070021);
    private const int ErrorDiskFull         = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull   = unchecked((int)0x80070027);

    public static StepFailure Describe(string stepTitle, Exception ex, InstallContext context)
    {
        var logHint = $" If this keeps happening, the setup log has details: {SetupLog.Path}";

        switch (ex)
        {
            case InstallStepException step:
                return new StepFailure(stepTitle, step.Message, step.Resolution);

            case UnauthorizedAccessException:
            case System.Security.SecurityException:
                return new StepFailure(stepTitle,
                    $"Setup doesn't have permission to change {context.InstallDir} (or a file in it).",
                    Elevation.IsElevated
                        ? "Make sure no antivirus or backup tool is locking the folder, then click Retry." + logHint
                        : "Cancel, then run Setup again and choose a folder in your user profile, or right-click Setup and choose \"Run as administrator\"." + logHint);

            case IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation:
                return new StepFailure(stepTitle,
                    "A file Setup needs to replace is in use by another program.",
                    "Close SqlVitals and any program using files in the install folder (for example File Explorer windows or an antivirus scan), then click Retry." + logHint);

            case IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull:
                return new StepFailure(stepTitle,
                    $"The drive for {context.InstallDir} is full.",
                    "Free up some space on that drive, then click Retry." + logHint);

            case PathTooLongException:
                return new StepFailure(stepTitle,
                    "The install folder path is too long for Windows.",
                    "Cancel and choose a shorter install folder, such as C:\\SqlVitals." + logHint);

            case InvalidDataException:
                return new StepFailure(stepTitle,
                    "The installation package is damaged.",
                    "Cancel, download SqlVitals Setup again from your official release page, and run the new copy." + logHint);

            default:
                return new StepFailure(stepTitle,
                    $"Something went wrong: {ex.Message}",
                    "Click Retry. If it fails again, cancel — Setup will undo its changes — and try restarting your PC." + logHint);
        }
    }
}
