using System.ComponentModel;

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

        /// <summary> Extra columns shown in the test plan view (comma separated keys). Changed from the Columns drop-down. </summary>
        [Browsable(false)]
        public string TestPlanColumns { get; set; } = "Duration,Verdict";

        /// <summary> The last test plans opened or saved, newest first, separated by '|'. </summary>
        [Browsable(false)]
        public string RecentPlans { get; set; } = "";

        /// <summary> Editor panels that were open and whether they were docked or floating ("Type:docked|Type:float"). </summary>
        [Browsable(false)]
        public string OpenPanels { get; set; } = "";

        /// <summary> Width of the Settings pane as a percentage of the window, as the user last left it. </summary>
        [Browsable(false)]
        public double SettingsPanePercent { get; set; } = 55;

        [Display("Follow Running Step","Scroll the test plan view so the running step is always visible.", Group: "Test Plan")]
        public bool FollowRunningStep { get; set; } = true;
    }
}
