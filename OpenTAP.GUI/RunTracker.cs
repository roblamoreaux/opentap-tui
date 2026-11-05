using System;
using System.ComponentModel;

namespace OpenTap.Gui
{
    /// <summary>
    /// Added to the result listeners of runs started from the GUI so the test plan view can show which step is running,
    /// durations and verdicts as they happen. Hidden from the Results settings.
    /// </summary>
    [Browsable(false)]
    [Display("GUI Run Tracker", "Used by the OpenTAP GUI to follow a running test plan.")]
    public class GuiRunTracker : ResultListener
    {
        internal Action<TestStepRun> StepStarted;
        internal Action<TestStepRun> StepCompleted;

        public GuiRunTracker()
        {
            Name = "GUI";
        }

        public override void OnTestStepRunStart(TestStepRun stepRun)
        {
            base.OnTestStepRunStart(stepRun);
            StepStarted?.Invoke(stepRun);
        }

        public override void OnTestStepRunCompleted(TestStepRun stepRun)
        {
            base.OnTestStepRunCompleted(stepRun);
            StepCompleted?.Invoke(stepRun);
        }
    }
}
