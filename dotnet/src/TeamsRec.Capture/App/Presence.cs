using System.Runtime.InteropServices;

namespace TeamsRec.Capture.App;

/// <summary>Is it fine to put text on the screen now? Windows knows when the user presents or runs something full
/// screen (SHQueryUserNotificationState); anything but "accepts notifications" counts as no.</summary>
public static class Presence
{
    private const int QunsAcceptsNotifications = 5;  // QUNS_ACCEPTS_NOTIFICATIONS

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    public static bool AcceptsNotifications()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state == QunsAcceptsNotifications;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;  // cannot tell: stay discreet
        }
    }
}
