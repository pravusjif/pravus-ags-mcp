using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Ui
{
    /// <summary>
    /// Serializes tool calls and runs them on the editor's main-window (WinForms UI) thread, where the editor's
    /// own model, panes and native room/sprite state live. The main form does not exist yet when the plugin is
    /// constructed (splash screen), so it is looked up per call through <c>mainForm</c>; until it exists, work
    /// falls back to a hidden control created on the constructing thread.
    /// </summary>
    public sealed class UiDispatcher : IToolExecutor, IDisposable
    {
        private readonly Control _fallback;
        private readonly Func<Control> _mainForm;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public UiDispatcher(Func<Control> mainForm = null)
        {
            _mainForm = mainForm;
            _fallback = new Control();
            _fallback.CreateControl();
            IntPtr forceHandle = _fallback.Handle; // binds the control to this thread
        }

        private Control Target
        {
            get
            {
                Control form = null;
                try { form = _mainForm?.Invoke(); }
                catch (Exception) { form = null; }
                return form != null && !form.IsDisposed && form.IsHandleCreated ? form : _fallback;
            }
        }

        public ToolResult Execute(Tool tool, Func<ToolResult> work)
        {
            if (!tool.RunOnUiThread) return work();
            return Invoke(work, tool.Timeout);
        }

        public T Invoke<T>(Func<T> work, TimeSpan timeout)
        {
            Control target = Target;
            if (!target.InvokeRequired) return RunWithSyncContext(work);

            if (!_gate.Wait(timeout))
                throw new ToolException("The editor is still busy with a previous MCP request. Try again shortly.");

            T result = default(T);
            Exception error = null;
            var done = new ManualResetEventSlim(false);
            try
            {
                target.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        if (IsModalDialogOpen())
                            throw new ToolException("The AGS editor is showing a dialog box. Close it in the editor, then retry.");
                        result = RunWithSyncContext(work);
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                    finally
                    {
                        done.Set();
                        _gate.Release();
                    }
                });
            }
            catch (Exception)
            {
                _gate.Release();
                throw;
            }

            // On timeout the work stays queued; the gate is released once it actually finishes.
            if (!done.Wait(timeout))
                throw new ToolException($"Timed out after {timeout.TotalSeconds:0}s waiting for the editor. It may be blocked by a dialog.");

            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }

        /// <summary>
        /// Runs tool work with a non-null SynchronizationContext installed on the current thread. Some editor
        /// save-path code calls TaskScheduler.FromCurrentSynchronizationContext() (the recent-games Debouncer),
        /// which throws if Current is null — and on the threads our tool work runs on, Current is often null.
        /// A plain base SynchronizationContext satisfies it and, unlike a WindowsFormsSynchronizationContext, is
        /// not uninstalled by WinForms when a form handle is destroyed mid-work (e.g. the save progress dialog).
        /// The previous context is restored afterwards.
        /// </summary>
        private static T RunWithSyncContext<T>(Func<T> work)
        {
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            try { return work(); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }

        /// <summary>
        /// True if a modal dialog is up on this (the UI) thread: a modal Form, or a Win32 dialog such as a
        /// MessageBox (class "#32770"), which is not in Application.OpenForms. Queued tool work would otherwise
        /// run re-entrantly inside the dialog's message loop, in the middle of whatever opened it.
        /// </summary>
        private static bool IsModalDialogOpen()
        {
            foreach (Form form in Application.OpenForms)
            {
                if (form.Visible && form.Modal) return true;
            }
            bool found = false;
            EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
            {
                var cls = new StringBuilder(32);
                GetClassName(hwnd, cls, cls.Capacity);
                if (IsWindowVisible(hwnd) && cls.ToString() == "#32770") { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public void Dispose()
        {
            _fallback.Dispose();
        }
    }
}
