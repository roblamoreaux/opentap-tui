using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using OpenTap.Cli;

namespace OpenTap.Gui
{
    /// <summary> "tap gui [plan]" - the Windows Forms equivalent of "tap tui". </summary>
    [Display("gui", "View, edit and run test plans in a Windows graphical interface.")]
    public class GuiAction : ICliAction
    {
        internal static readonly TraceSource Log = OpenTap.Log.CreateSource("GUI");

        /// <summary> The top-level window, once it exists. Used to marshal user input requests onto the UI thread. </summary>
        internal static Form MainForm { get; private set; }

        [UnnamedCommandLineArgument("plan")]
        public string PlanPath { get; set; }

        public int Execute(CancellationToken cancellationToken) => RunWindow(() => new MainForm(PlanPath), cancellationToken);

        /// <summary> Shows a window as the application's main window until it is closed. Shared by all the gui actions. </summary>
        internal static int RunWindow(Func<Form> createWindow, CancellationToken cancellationToken)
        {
            // Start collecting log messages before anything else happens so the log panel shows startup messages too.
            LogStore.EnsureListening();

            // Stop OpenTAP from asking for user input on the console - ask with dialogs instead.
            UserInput.SetInterface(null);
            UserInput.SetInterface(new GuiUserInput());

            HideConsoleIfWeOwnIt();

            Exception error = null;
            // WinForms (file dialogs, clipboard, drag/drop) needs a single-threaded apartment thread.
            var uiThread = new Thread(() =>
            {
                try
                {
                    try
                    {
                        Application.SetHighDpiMode(HighDpiMode.SystemAware);
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already initialized in this process - fine.
                    }

                    var form = createWindow();
                    MainForm = form;
                    using (cancellationToken.Register(() =>
                           {
                               if (form.IsHandleCreated && !form.IsDisposed)
                                   form.BeginInvoke(new Action(form.Close));
                           }))
                    {
                        Application.Run(form);
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    MainForm = null;
                }
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Name = "OpenTAP GUI";
            uiThread.Start();
            uiThread.Join();

            if (error != null)
            {
                Log.Error("The GUI closed because of an unexpected error: {0}", error.Message);
                Log.Debug(error);
                return 1;
            }
            return 0;
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// When tap.exe is started from a shortcut or Explorer it gets a console window of its own. Hide that one,
        /// but leave the console alone when "tap gui" was typed into an existing terminal.
        /// </summary>
        static void HideConsoleIfWeOwnIt()
        {
            try
            {
                var console = GetConsoleWindow();
                if (console == IntPtr.Zero)
                    return;
                if (GetConsoleProcessList(new uint[2], 2) == 1)
                    ShowWindow(console, 0 /* SW_HIDE */);
            }
            catch
            {
                // Cosmetic only.
            }
        }
    }

    /// <summary> "tap gui-pm" - the Windows Forms equivalent of "tap tui-pm". </summary>
    [Display("gui-pm", "Open the package manager in a Windows graphical interface.")]
    public class GuiPackageManagerAction : ICliAction
    {
        public int Execute(CancellationToken cancellationToken) => GuiAction.RunWindow(() => new PackageManagerForm(), cancellationToken);
    }

    /// <summary> "tap gui-results" - the Windows Forms equivalent of "tap tui-results". </summary>
    [Display("gui-results", "Open the results viewer in a Windows graphical interface.")]
    public class GuiResultsAction : ICliAction
    {
        public int Execute(CancellationToken cancellationToken) => GuiAction.RunWindow(() => new ResultsViewerForm(), cancellationToken);
    }
}
