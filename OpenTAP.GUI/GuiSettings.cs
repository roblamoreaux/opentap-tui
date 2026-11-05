namespace OpenTap.Gui
{
    /// <summary> Settings for the GUI itself. Shown in the "Settings" menu next to the other OpenTAP settings. </summary>
    [Display("GUI Settings")]
    public class GuiSettings : ComponentSettings<GuiSettings>
    {
        [Display("Scrollback Limit", "Maximum number of messages kept in the log panel.", Group: "Log Panel")]
        public Enabled<int> LogScrollbackLimit { get; set; } = new Enabled<int> { IsEnabled = true, Value = 100000 };

        [Display("Clear on Run", "Clear the log panel every time a test plan run starts.", Group: "Log Panel")]
        public bool ClearOnRun { get; set; }
    }
}
