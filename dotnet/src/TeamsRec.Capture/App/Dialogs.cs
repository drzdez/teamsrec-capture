using System.Media;
using System.Runtime.InteropServices;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.App;

/// <summary>Native Yes/No boxes with a timeout and the small title input box. The boxes are called from the
/// monitor thread while a recording is already running, so they must never block the tray or the loop's
/// decisions for long: MessageBoxTimeoutW closes itself and the default wins.</summary>
public static class Dialogs
{
    private const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_DEFBUTTON2 = 0x100,
                       MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    private const int IDYES = 6, IDNO = 7;  // 32000 = timed out

    // Undocumented but present in user32 since Windows XP; the prototype relies on it too.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxTimeoutW")]
    private static extern int MessageBoxTimeout(IntPtr hWnd, string text, string caption, uint type,
                                                ushort languageId, uint milliseconds);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    /// <summary>Test seam: replaces the native box (text, caption, flags, timeout ms) -> result code.</summary>
    internal static Func<string, string, uint, int, int>? BoxOverride;

    private static int Box(string text, string caption, uint flags, int timeoutS)
    {
        if (BoxOverride is not null) return BoxOverride(text, caption, flags, timeoutS * 1000);
        try { SystemSounds.Asterisk.Play(); } catch (Exception) { }
        return MessageBoxTimeout(IntPtr.Zero, text, caption, flags, 0, (uint)Math.Max(1, timeoutS) * 1000);
    }

    /// <summary>Maps a MessageBoxTimeout result to yes/no: an explicit button wins, a timeout (32000) or an
    /// error takes the default.</summary>
    internal static bool Answer(int result, bool defaultYes) =>
        result == IDYES || (result != IDNO && defaultYes);

    /// <summary>Native Yes/No box with a timeout (ask_yes_no). On timeout the default wins.</summary>
    public static bool YesNo(string text, int timeoutS = 60, bool defaultYes = false, string caption = "teamsrec")
    {
        uint flags = MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST | (defaultYes ? 0 : MB_DEFBUTTON2);
        try
        {
            return Answer(Box(text, caption, flags, timeoutS), defaultYes);
        }
        catch (Exception e)
        {
            FileLog.Exception("yes/no box", e);
            return defaultYes;
        }
    }

    /// <summary>The text of the discard box; the automatic outcome depends on prompt_default.</summary>
    internal static string DiscardText(string title, int timeoutS, string promptDefault) =>
        $"Nahrávám: {title}\n\nZahodit tuto nahrávku?\n" +
        $"Ano = nenahrávat a smazat, Ne = nechat nahrávat (za {timeoutS} s automaticky " +
        $"{(promptDefault == "skip" ? "zahodit" : "nechat")}).";

    /// <summary>Recording has already started; ask whether to throw it away (ask_discard). True = discard.
    /// Default (Enter / timeout) = keep, unless prompt_default is "skip". A failing box keeps the recording:
    /// losing a meeting is worse than keeping one too many.</summary>
    public static bool AskDiscard(string title, string promptDefault, int timeoutS = AppLogic.PromptTimeoutS)
    {
        bool discardByDefault = promptDefault == "skip";
        uint flags = MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST | (discardByDefault ? 0 : MB_DEFBUTTON2);
        try
        {
            return Answer(Box(DiscardText(title, timeoutS, promptDefault), "teamsrec", flags, timeoutS),
                          discardByDefault);
        }
        catch (Exception e)
        {
            FileLog.Exception("discard box", e);
            return false;
        }
    }

    /// <summary>Title of the foreground window: a sensible default title for a playback recording (the player
    /// window usually carries the recording's name).</summary>
    public static string ForegroundWindowTitle()
    {
        try
        {
            var sb = new System.Text.StringBuilder(512);
            return GetWindowText(GetForegroundWindow(), sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Small always-on-top input box (simpledialog.askstring). Returns null on Cancel / empty.
    /// Must run on a thread with a message loop (the tray UI thread).</summary>
    public static string? InputBox(string prompt, string initial, string caption = "teamsrec")
    {
        using var form = new Form
        {
            Text = caption,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            AutoScaleMode = AutoScaleMode.Font,
            ClientSize = new Size(420, 120),
        };
        var label = new Label { Text = prompt, Left = 12, Top = 12, Width = 396, AutoSize = false, Height = 20 };
        var box = new TextBox { Text = initial, Left = 12, Top = 38, Width = 396 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Top = 78, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Top = 78, Width = 75 };
        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { form.Activate(); box.Focus(); box.SelectAll(); };
        if (form.ShowDialog() != DialogResult.OK) return null;
        var text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
