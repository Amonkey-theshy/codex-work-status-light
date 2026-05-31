using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WorkStatusLight
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "CodexWorkStatusLight.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Run(args);
                GC.KeepAlive(mutex);
            }
        }

        private static void Run(string[] args)
        {
            try
            {
                Logger.Write("Program starting");
                bool manual = args.Any(a => String.Equals(a, "--manual", StringComparison.OrdinalIgnoreCase));
                bool noSnap = args.Any(a => String.Equals(a, "--no-snap", StringComparison.OrdinalIgnoreCase));

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new StatusLightForm(!manual, !noSnap));
                Logger.Write("Program exiting normally");
            }
            catch (Exception ex)
            {
                Logger.Write("Fatal: " + ex);
                MessageBox.Show(ex.ToString(), "Codex Work Status Light", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class StatusLightForm : Form
    {
        private const string TargetProcessName = "Codex";
        private const int Gap = 3;
        private const int InnerGap = 5;
        private const int LightWindowWidth = 58;
        private const int LightWindowHeight = 154;
        private const double BusyPercent = 2.0;
        private const double AppServerBusyPercent = 3.0;
        private const double MinBusyAboveBaseline = 1.2;
        private const double MinAppAboveBaseline = 1.5;
        private const double StrongBusyPercent = 4.0;
        private const double StrongAppServerBusyPercent = 5.0;
        private const int BaselineWarmupSamples = 6;
        private const int StrongBusySamplesAfterDone = 3;
        private const int QuietSamplesBeforeDone = 5;
        private const int QuietBeforeDoneSeconds = 10;
        private const int DoneHoldSeconds = 25;
        private const int ExternalStatusHoldSeconds = 12;
        private const int ExternalForegroundHoldSeconds = 90;
        private const int SessionActiveFreshSeconds = 900;
        private const int SessionFileFreshHours = 24;
        private const int SessionFileLimit = 30;

        private readonly string statusPath;
        private readonly System.Windows.Forms.Timer statusTimer;
        private readonly System.Windows.Forms.Timer snapTimer;
        private readonly System.Windows.Forms.Timer flashTimer;
        private readonly ToolTip tooltip;
        private readonly ToolStripMenuItem autoItem;
        private readonly ToolStripMenuItem snapItem;
        private readonly Dictionary<string, SessionFileSnapshot> sessionFileCache = new Dictionary<string, SessionFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly string sessionsRoot;

        private string currentState = "waiting";
        private string currentLabel = "Waiting";
        private string currentMessage = "Codex is waiting";
        private bool autoDetect;
        private bool snapToCodex;
        private bool dragging;
        private bool hasSeenBusy;
        private bool doneAnnounced;
        private bool doneFlashVisible = true;
        private Point lastLoggedSnap = new Point(Int32.MinValue, Int32.MinValue);
        private Point dragStartMouse;
        private Point dragStartWindow;
        private DateTime lastBusyAt = DateTime.MinValue;
        private DateTime lastExternalForegroundAt = DateTime.MinValue;
        private DateTime doneAnnouncedAt = DateTime.MinValue;
        private DateTime lastExternalStatusAt = DateTime.MinValue;
        private DateTime lastTargetRefresh = DateTime.MinValue;
        private DateTime lastSessionScanAt = DateTime.MinValue;
        private DateTime lastSessionCompletionSeen = DateTime.MinValue;
        private CpuSample lastCpuSample;
        private CpuSample lastAppServerCpuSample;
        private Process cachedTarget;
        private int doneFlashTicksRemaining;
        private int quietSampleCount;
        private int strongBusyAfterDoneCount;
        private int baselineSamples;
        private double totalCpuBaseline = 1.0;
        private double appServerCpuBaseline = 1.0;
        private SessionSnapshot cachedSessionSnapshot;
        private bool sessionTrackingInitialized;

        public StatusLightForm(bool autoDetect, bool snapToCodex)
        {
            this.autoDetect = autoDetect;
            this.snapToCodex = snapToCodex;
            statusPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "status.json");
            sessionsRoot = Environment.GetEnvironmentVariable("CODEX_STATUS_SESSIONS_ROOT");
            if (String.IsNullOrWhiteSpace(sessionsRoot))
            {
                sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            }

            Text = T.AppTitle;
            ClientSize = new Size(LightWindowWidth, LightWindowHeight);
            MinimumSize = new Size(1, 1);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(40, 120);
            BackColor = Color.Fuchsia;
            TransparencyKey = Color.Fuchsia;
            DoubleBuffered = true;
            Logger.Write("Form constructed");

            tooltip = new ToolTip();
            tooltip.SetToolTip(this, T.AppTitle);

            var menu = new ContextMenuStrip();
            autoItem = new ToolStripMenuItem(T.AutoDetect) { CheckOnClick = true, Checked = this.autoDetect };
            snapItem = new ToolStripMenuItem(T.SnapToCodex) { CheckOnClick = true, Checked = this.snapToCodex };
            var waitItem = new ToolStripMenuItem(T.RedWaiting);
            var workItem = new ToolStripMenuItem(T.YellowWorking);
            var doneItem = new ToolStripMenuItem(T.GreenDone);
            var exitItem = new ToolStripMenuItem(T.Exit);

            autoItem.CheckedChanged += delegate { this.autoDetect = autoItem.Checked; };
            snapItem.CheckedChanged += delegate { this.snapToCodex = snapItem.Checked; };
            waitItem.Click += delegate { ManualState("waiting", T.ManualWaiting); };
            workItem.Click += delegate { ManualState("working", T.ManualWorking); };
            doneItem.Click += delegate { ManualState("done", T.ManualDone); };
            exitItem.Click += delegate { Close(); };

            menu.Items.Add(autoItem);
            menu.Items.Add(snapItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(waitItem);
            menu.Items.Add(workItem);
            menu.Items.Add(doneItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            ContextMenuStrip = menu;

            ReadStatus();
            statusTimer = new System.Windows.Forms.Timer { Interval = 800 };
            statusTimer.Tick += delegate
            {
                ReadStatus();
                UpdateAutoStatus();
                tooltip.SetToolTip(this, currentLabel + " - " + currentMessage);
                Invalidate();
            };
            statusTimer.Start();

            snapTimer = new System.Windows.Forms.Timer { Interval = 60 };
            snapTimer.Tick += delegate { UpdatePosition(); };
            snapTimer.Start();

            flashTimer = new System.Windows.Forms.Timer { Interval = 180 };
            flashTimer.Tick += delegate
            {
                if (doneFlashTicksRemaining <= 0)
                {
                    doneFlashVisible = true;
                    flashTimer.Stop();
                    Invalidate();
                    return;
                }

                doneFlashVisible = !doneFlashVisible;
                doneFlashTicksRemaining--;
                Invalidate();
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Logger.Write("Form shown at " + Location.X + "," + Location.Y + " handle=" + Handle);
            UpdatePosition();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Logger.Write("Handle created: " + Handle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(TransparencyKey);

            using (GraphicsPath bodyPath = RoundPath(new RectangleF(5, 5, 48, 144), 13))
            using (var bodyBrush = new LinearGradientBrush(new RectangleF(5, 5, 48, 144), Color.FromArgb(48, 52, 55), Color.FromArgb(12, 14, 15), 90))
            using (var borderPen = new Pen(Color.FromArgb(6, 8, 9), 2))
            {
                g.FillPath(bodyBrush, bodyPath);
                g.DrawPath(borderPen, bodyPath);
            }

            DrawLight(g, 13, 16, 32, Color.FromArgb(235, 53, 70), currentState == "waiting");
            DrawLight(g, 13, 61, 32, Color.FromArgb(255, 200, 69), currentState == "working");
            DrawLight(g, 13, 106, 32, Color.FromArgb(32, 195, 106), currentState == "done" && doneFlashVisible);
        }

        protected override void WndProc(ref Message m)
        {
            const int WmGetMinMaxInfo = 0x0024;
            if (m.Msg == WmGetMinMaxInfo)
            {
                base.WndProc(ref m);
                var info = (MinMaxInfo)Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
                info.ptMinTrackSize.X = 1;
                info.ptMinTrackSize.Y = 1;
                Marshal.StructureToPtr(info, m.LParam, false);
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            dragging = true;
            dragStartMouse = Cursor.Position;
            dragStartWindow = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;

            Point mouse = Cursor.Position;
            Location = new Point(dragStartWindow.X + mouse.X - dragStartMouse.X, dragStartWindow.Y + mouse.Y - dragStartMouse.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                dragging = false;
                UpdatePosition();
            }
        }

        private void ManualState(string state, string message)
        {
            autoDetect = false;
            autoItem.Checked = false;
            WriteStatus(state, message, "manual");
            Invalidate();
        }

        private void UpdateAutoStatus()
        {
            if (!autoDetect) return;

            if ((DateTime.Now - lastExternalStatusAt).TotalSeconds < ExternalStatusHoldSeconds)
            {
                return;
            }

            DateTime now = DateTime.Now;
            SessionSnapshot sessions = GetSessionSnapshot(now);
            if (sessions.IsAvailable)
            {
                UpdateSessionAwareStatus(sessions, now);
                return;
            }

            ActivitySnapshot activity = GetCodexActivitySnapshot();

            UpdateActivityBaseline(activity);

            if (doneAnnounced)
            {
                if (activity.IsStrongBusy)
                {
                    strongBusyAfterDoneCount++;
                }
                else
                {
                    strongBusyAfterDoneCount = 0;
                }

                if (strongBusyAfterDoneCount >= StrongBusySamplesAfterDone)
                {
                    doneAnnounced = false;
                    strongBusyAfterDoneCount = 0;
                }
                else if ((now - doneAnnouncedAt).TotalSeconds < DoneHoldSeconds)
                {
                    WriteStatus("done", T.WorkJustFinished, "auto");
                    return;
                }
                else
                {
                    doneAnnounced = false;
                    hasSeenBusy = false;
                    quietSampleCount = 0;
                    strongBusyAfterDoneCount = 0;
                    WriteStatus("waiting", T.CodexWaiting, "auto");
                    return;
                }
            }

            if (activity.IsBusy)
            {
                lastBusyAt = now;
                hasSeenBusy = true;
                doneAnnounced = false;
                quietSampleCount = 0;
                WriteStatus("working", String.Format(CultureInfo.InvariantCulture, T.HybridWorkingFormat, activity.TotalCpuPercent, activity.AppServerCpuPercent, totalCpuBaseline, appServerCpuBaseline), "auto");
                return;
            }

            if (hasSeenBusy)
            {
                quietSampleCount++;
            }

            ForegroundSnapshot foreground = GetForegroundSnapshot();
            if (hasSeenBusy && foreground.IsExternalApp && (now - lastBusyAt).TotalSeconds < ExternalForegroundHoldSeconds)
            {
                lastExternalForegroundAt = now;
                WriteStatus("working", String.Format(CultureInfo.InvariantCulture, T.OperatingComputerFormat, foreground.ProcessName), "auto");
                return;
            }

            if (hasSeenBusy && (now - lastExternalForegroundAt).TotalSeconds < QuietBeforeDoneSeconds)
            {
                WriteStatus("working", T.OperatingComputer, "auto");
                return;
            }

            bool stillThinking = hasSeenBusy &&
                ((now - lastBusyAt).TotalSeconds < QuietBeforeDoneSeconds ||
                 quietSampleCount < QuietSamplesBeforeDone);

            if (stillThinking)
            {
                WriteStatus("working", T.CodexThinking, "auto");
                return;
            }

            if (hasSeenBusy && (now - lastBusyAt).TotalSeconds < QuietBeforeDoneSeconds + DoneHoldSeconds)
            {
                doneAnnounced = true;
                doneAnnouncedAt = now;
                WriteStatus("done", T.WorkJustFinished, "auto");
                return;
            }

            hasSeenBusy = false;
            quietSampleCount = 0;
            WriteStatus("waiting", T.CodexWaiting, "auto");
        }

        private void UpdateSessionAwareStatus(SessionSnapshot sessions, DateTime now)
        {
            bool hasNewCompletion = sessions.LatestCompletionAt > lastSessionCompletionSeen;

            if (!sessionTrackingInitialized)
            {
                sessionTrackingInitialized = true;
                lastSessionCompletionSeen = sessions.LatestCompletionAt;
                hasNewCompletion = false;
            }
            else if (hasNewCompletion)
            {
                lastSessionCompletionSeen = sessions.LatestCompletionAt;
            }

            if (sessions.HasActiveSession)
            {
                doneAnnounced = false;
                hasSeenBusy = true;
                WriteStatus("working", T.SessionWorking, "session");
                return;
            }

            if (hasNewCompletion)
            {
                doneAnnounced = true;
                doneAnnouncedAt = now;
                WriteStatus("done", T.WorkJustFinished, "session");
                return;
            }

            if (!doneAnnounced &&
                sessions.LatestCompletionAt != DateTime.MinValue &&
                (now - sessions.LatestCompletionAt).TotalSeconds < DoneHoldSeconds)
            {
                doneAnnounced = true;
                doneAnnouncedAt = sessions.LatestCompletionAt;
            }

            if (doneAnnounced && (now - doneAnnouncedAt).TotalSeconds < DoneHoldSeconds)
            {
                WriteStatus("done", T.WorkJustFinished, "session");
                return;
            }

            doneAnnounced = false;
            hasSeenBusy = false;
            WriteStatus("waiting", T.CodexWaiting, "session");
        }

        private SessionSnapshot GetSessionSnapshot(DateTime now)
        {
            if ((now - lastSessionScanAt).TotalMilliseconds < 700 && cachedSessionSnapshot != null)
            {
                return cachedSessionSnapshot;
            }

            lastSessionScanAt = now;
            if (!Directory.Exists(sessionsRoot))
            {
                cachedSessionSnapshot = SessionSnapshot.Unavailable();
                return cachedSessionSnapshot;
            }

            try
            {
                DateTime fileCutoff = now.AddHours(-SessionFileFreshHours);
                string[] files = Directory.GetFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
                    .Where(path => File.GetLastWriteTime(path) >= fileCutoff)
                    .OrderByDescending(path => File.GetLastWriteTime(path))
                    .Take(SessionFileLimit)
                    .ToArray();

                DateTime latestCompletionAt = DateTime.MinValue;
                bool hasActiveSession = false;

                foreach (string file in files)
                {
                    SessionFileSnapshot fileSnapshot = GetSessionFileSnapshot(file);
                    if (fileSnapshot == null) continue;

                    if (fileSnapshot.LatestCompletionAt > latestCompletionAt)
                    {
                        latestCompletionAt = fileSnapshot.LatestCompletionAt;
                    }

                    bool unfinished = fileSnapshot.LatestUserAt > fileSnapshot.LatestCompletionAt;
                    bool fresh = fileSnapshot.LatestEventAt != DateTime.MinValue &&
                        (now - fileSnapshot.LatestEventAt).TotalSeconds < SessionActiveFreshSeconds;
                    if (unfinished && fresh)
                    {
                        hasActiveSession = true;
                    }
                }

                cachedSessionSnapshot = new SessionSnapshot(true, hasActiveSession, latestCompletionAt);
                return cachedSessionSnapshot;
            }
            catch (Exception ex)
            {
                Logger.Write("Session scan unavailable: " + ex.Message);
                cachedSessionSnapshot = SessionSnapshot.Unavailable();
                return cachedSessionSnapshot;
            }
        }

        private SessionFileSnapshot GetSessionFileSnapshot(string path)
        {
            FileInfo info = new FileInfo(path);
            SessionFileSnapshot cached;
            if (sessionFileCache.TryGetValue(path, out cached) &&
                cached.Length == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                return cached;
            }

            DateTime latestUserAt = DateTime.MinValue;
            DateTime latestCompletionAt = DateTime.MinValue;
            DateTime latestEventAt = DateTime.MinValue;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    DateTime timestamp = GetJsonTimestamp(line);
                    if (timestamp == DateTime.MinValue) continue;
                    if (timestamp > latestEventAt) latestEventAt = timestamp;

                    bool userMessage = line.IndexOf("\"type\":\"user_message\"", StringComparison.Ordinal) >= 0 ||
                        (line.IndexOf("\"type\":\"message\"", StringComparison.Ordinal) >= 0 &&
                         line.IndexOf("\"role\":\"user\"", StringComparison.Ordinal) >= 0);
                    if (userMessage && timestamp > latestUserAt)
                    {
                        latestUserAt = timestamp;
                    }

                    bool completion = line.IndexOf("\"type\":\"task_complete\"", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("\"phase\":\"final_answer\"", StringComparison.Ordinal) >= 0;
                    if (completion && timestamp > latestCompletionAt)
                    {
                        latestCompletionAt = timestamp;
                    }
                }
            }

            var snapshot = new SessionFileSnapshot(info.Length, info.LastWriteTimeUtc, latestUserAt, latestCompletionAt, latestEventAt);
            sessionFileCache[path] = snapshot;
            return snapshot;
        }

        private static DateTime GetJsonTimestamp(string line)
        {
            const string prefix = "\"timestamp\":\"";
            int start = line.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) return DateTime.MinValue;
            start += prefix.Length;
            int end = line.IndexOf('"', start);
            if (end <= start) return DateTime.MinValue;

            DateTime parsed;
            if (DateTime.TryParse(line.Substring(start, end - start), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToLocalTime();
            }

            return DateTime.MinValue;
        }

        private void UpdatePosition()
        {
            if (!snapToCodex) return;

            Process target = GetCachedCodexWindow();
            if (target == null)
            {
                if (Visible)
                {
                    Hide();
                    Logger.Write("Hidden: Codex window not found");
                }
                return;
            }

            if (!Visible)
            {
                Show();
                Logger.Write("Shown: Codex window found");
            }

            Rect rect;
            if (!NativeMethods.GetWindowRect(target.MainWindowHandle, out rect)) return;

            Rectangle screen = Screen.FromHandle(target.MainWindowHandle).WorkingArea;
            int x;
            if (rect.Right + Gap + Width <= screen.Right)
            {
                x = rect.Right + Gap;
            }
            else if (rect.Left - Gap - Width >= screen.Left)
            {
                x = rect.Left - Gap - Width;
            }
            else
            {
                x = Math.Min(screen.Right - Width - 8, rect.Right - Width - InnerGap);
                if (x < screen.Left + 8)
                {
                    x = screen.Left + 8;
                }
            }

            int y = rect.Top + 72;
            if (y + Height > screen.Bottom)
            {
                y = screen.Bottom - Height - 8;
            }
            if (y < screen.Top)
            {
                y = screen.Top + 8;
            }

            x = Math.Max(screen.Left + 8, Math.Min(x, screen.Right - Width - 8));
            y = Math.Max(screen.Top + 8, Math.Min(y, screen.Bottom - Height - 8));

            Location = new Point(x, y);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost, x, y, Width, Height, NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            if (lastLoggedSnap.X != x || lastLoggedSnap.Y != y)
            {
                lastLoggedSnap = new Point(x, y);
                Logger.Write("Snapped to " + x + "," + y + " target=" + target.Id);
            }
        }

        private Process GetCachedCodexWindow()
        {
            bool shouldRefresh = cachedTarget == null ||
                cachedTarget.HasExited ||
                cachedTarget.MainWindowHandle == IntPtr.Zero ||
                (DateTime.Now - lastTargetRefresh).TotalSeconds >= 2;

            if (shouldRefresh)
            {
                cachedTarget = FindCodexWindow();
                lastTargetRefresh = DateTime.Now;
            }

            return cachedTarget;
        }

        private static Process FindCodexWindow()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(TargetProcessName);
            }
            catch
            {
                return null;
            }

            return processes
                .Where(p => p.MainWindowHandle != IntPtr.Zero && NativeMethods.IsWindowVisible(p.MainWindowHandle))
                .OrderBy(p => String.Equals(p.MainWindowTitle, "Codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p.Id)
                .FirstOrDefault();
        }

        private double GetCodexCpuPercent()
        {
            return GetCpuPercentForProcesses(process =>
                String.Equals(process.ProcessName, "Codex", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(process.ProcessName, "codex", StringComparison.OrdinalIgnoreCase),
                ref lastCpuSample);
        }

        private ActivitySnapshot GetCodexActivitySnapshot()
        {
            double totalPercent = GetCodexCpuPercent();
            double appServerPercent = GetCpuPercentForProcesses(IsCodexAppServerProcess, ref lastAppServerCpuSample);
            bool appServerRunning = false;

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (IsCodexAppServerProcess(process))
                    {
                        appServerRunning = true;
                        break;
                    }
                }
                catch
                {
                }
            }

            bool baselineReady = baselineSamples >= BaselineWarmupSamples;
            bool totalBusy = totalPercent >= BusyPercent && (!baselineReady || totalPercent >= totalCpuBaseline + MinBusyAboveBaseline);
            bool appBusy = appServerPercent >= AppServerBusyPercent &&
                totalPercent >= 1.5 &&
                (!baselineReady || appServerPercent >= appServerCpuBaseline + MinAppAboveBaseline);
            bool busy = totalBusy || appBusy;
            bool strongBusy = totalPercent >= StrongBusyPercent ||
                (appServerPercent >= StrongAppServerBusyPercent && totalPercent >= BusyPercent);
            return new ActivitySnapshot(totalPercent, appServerPercent, appServerRunning, busy, strongBusy);
        }

        private void UpdateActivityBaseline(ActivitySnapshot activity)
        {
            if (activity.IsBusy || hasSeenBusy)
            {
                return;
            }

            double alpha = baselineSamples < BaselineWarmupSamples ? 0.35 : 0.08;
            totalCpuBaseline = Smooth(totalCpuBaseline, activity.TotalCpuPercent, alpha);
            appServerCpuBaseline = Smooth(appServerCpuBaseline, activity.AppServerCpuPercent, alpha);
            baselineSamples++;
        }

        private static double Smooth(double current, double next, double alpha)
        {
            if (Double.IsNaN(current) || Double.IsInfinity(current))
            {
                return next;
            }

            return current + ((next - current) * alpha);
        }

        private static bool IsCodexAppServerProcess(Process process)
        {
            try
            {
                return String.Equals(process.ProcessName, "codex", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private double GetCpuPercentForProcesses(Func<Process, bool> predicate, ref CpuSample sample)
        {
            DateTime now = DateTime.Now;
            double cpuSeconds = 0;

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (predicate(process))
                    {
                        cpuSeconds += process.TotalProcessorTime.TotalSeconds;
                    }
                }
                catch
                {
                }
            }

            if (sample == null)
            {
                sample = new CpuSample(now, cpuSeconds);
                return 0;
            }

            double elapsed = (now - sample.Time).TotalSeconds;
            if (elapsed <= 0) return 0;

            double delta = Math.Max(0, cpuSeconds - sample.CpuSeconds);
            sample = new CpuSample(now, cpuSeconds);
            return (delta / elapsed / Environment.ProcessorCount) * 100.0;
        }

        private ForegroundSnapshot GetForegroundSnapshot()
        {
            IntPtr foregroundHandle = NativeMethods.GetForegroundWindow();
            if (foregroundHandle == IntPtr.Zero)
            {
                return new ForegroundSnapshot(null, false);
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(foregroundHandle, out processId);

            try
            {
                Process process = Process.GetProcessById((int)processId);
                string name = process.ProcessName;
                bool isOurLight = process.Id == Process.GetCurrentProcess().Id;
                bool isCodex = String.Equals(name, "Codex", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(name, "codex", StringComparison.OrdinalIgnoreCase);
                return new ForegroundSnapshot(name, !isCodex && !isOurLight);
            }
            catch
            {
                return new ForegroundSnapshot(null, false);
            }
        }

        private void ReadStatus()
        {
            if (!File.Exists(statusPath))
            {
                WriteStatus("waiting", T.CodexWaiting, "auto");
                return;
            }

            try
            {
                var serializer = new JavaScriptSerializer();
                StatusPayload payload = serializer.Deserialize<StatusPayload>(File.ReadAllText(statusPath, Encoding.UTF8));
                StatusPayload normalized = StatusPayload.For(payload == null ? "waiting" : payload.state, payload == null ? null : payload.message);
                string source = payload == null ? "auto" : payload.source;
                if (!IsAutomaticSource(source) &&
                    (normalized.state != currentState || normalized.message != currentMessage))
                {
                    lastExternalStatusAt = DateTime.Now;
                }
                string previousState = currentState;
                currentState = normalized.state;
                currentLabel = normalized.label;
                currentMessage = normalized.message;
                if (previousState == "working" && currentState == "done")
                {
                    StartDoneFlash();
                }
            }
            catch
            {
                currentState = "waiting";
                currentLabel = T.Waiting;
                currentMessage = T.CodexWaiting;
            }
        }

        private void WriteStatus(string state, string message, string source)
        {
            StatusPayload payload = StatusPayload.For(state, message);
            payload.source = source;
            if (currentState == payload.state && currentMessage == payload.message) return;

            string previousState = currentState;
            currentState = payload.state;
            currentLabel = payload.label;
            currentMessage = payload.message;

            if (previousState == "working" && payload.state == "done")
            {
                StartDoneFlash();
            }

            try
            {
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(statusPath, serializer.Serialize(payload), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static bool IsAutomaticSource(string source)
        {
            return String.Equals(source, "auto", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(source, "session", StringComparison.OrdinalIgnoreCase);
        }

        private void StartDoneFlash()
        {
            doneFlashTicksRemaining = 10;
            doneFlashVisible = true;
            flashTimer.Stop();
            flashTimer.Start();
            Invalidate();
        }

        private static GraphicsPath RoundPath(RectangleF rect, float radius)
        {
            float diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawLight(Graphics g, int x, int y, int size, Color color, bool active)
        {
            Rectangle outer = new Rectangle(x, y, size, size);
            using (var rimBrush = new SolidBrush(Color.FromArgb(6, 8, 9)))
            {
                g.FillEllipse(rimBrush, outer);
            }

            if (active)
            {
                for (int i = 3; i >= 1; i--)
                {
                    int grow = 5 * i;
                    int alpha = 28 + (18 * (4 - i));
                    Rectangle glowRect = new Rectangle(x - grow, y - grow, size + grow * 2, size + grow * 2);
                    using (var glowBrush = new SolidBrush(Color.FromArgb(alpha, color)))
                    {
                        g.FillEllipse(glowBrush, glowRect);
                    }
                }
            }

            Rectangle inner = new Rectangle(x + 6, y + 6, size - 12, size - 12);
            Color baseColor = active ? color : Color.FromArgb(38, 43, 45);
            using (var lensBrush = new LinearGradientBrush(inner, baseColor, Color.FromArgb(13, 15, 16), 65))
            {
                g.FillEllipse(lensBrush, inner);
            }

            Rectangle shine = new Rectangle(x + 13, y + 11, 11, 7);
            using (var shineBrush = new SolidBrush(Color.FromArgb(active ? 155 : 30, 255, 255, 255)))
            {
                g.FillEllipse(shineBrush, shine);
            }
        }
    }

    internal sealed class StatusPayload
    {
        public string state { get; set; }
        public string label { get; set; }
        public string color { get; set; }
        public string message { get; set; }
        public string updatedAt { get; set; }
        public string source { get; set; }

        public static StatusPayload For(string value, string message)
        {
            string state = Normalize(value);
            string label = T.Waiting;
            string color = "red";
            string defaultMessage = T.CodexWaiting;

            if (state == "working")
            {
                label = T.Working;
                color = "yellow";
                defaultMessage = T.CodexWorking;
            }
            else if (state == "done")
            {
                label = T.Done;
                color = "green";
                defaultMessage = T.WorkJustFinished;
            }

            return new StatusPayload
            {
                state = state,
                label = label,
                color = color,
                message = String.IsNullOrWhiteSpace(message) ? defaultMessage : message,
                updatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                source = "auto"
            };
        }

        private static string Normalize(string value)
        {
            value = (value ?? String.Empty).Trim().ToLowerInvariant();
            if (value == "working" || value == "work" || value == "busy" || value == "yellow") return "working";
            if (value == "done" || value == "complete" || value == "completed" || value == "finish" || value == "finished" || value == "green") return "done";
            return "waiting";
        }
    }

    internal sealed class CpuSample
    {
        public CpuSample(DateTime time, double cpuSeconds)
        {
            Time = time;
            CpuSeconds = cpuSeconds;
        }

        public DateTime Time { get; private set; }
        public double CpuSeconds { get; private set; }
    }

    internal sealed class ActivitySnapshot
    {
        public ActivitySnapshot(double totalCpuPercent, double appServerCpuPercent, bool appServerRunning, bool isBusy, bool isStrongBusy)
        {
            TotalCpuPercent = totalCpuPercent;
            AppServerCpuPercent = appServerCpuPercent;
            AppServerRunning = appServerRunning;
            IsBusy = isBusy;
            IsStrongBusy = isStrongBusy;
        }

        public double TotalCpuPercent { get; private set; }
        public double AppServerCpuPercent { get; private set; }
        public bool AppServerRunning { get; private set; }
        public bool IsBusy { get; private set; }
        public bool IsStrongBusy { get; private set; }
    }

    internal sealed class SessionSnapshot
    {
        public SessionSnapshot(bool isAvailable, bool hasActiveSession, DateTime latestCompletionAt)
        {
            IsAvailable = isAvailable;
            HasActiveSession = hasActiveSession;
            LatestCompletionAt = latestCompletionAt;
        }

        public bool IsAvailable { get; private set; }
        public bool HasActiveSession { get; private set; }
        public DateTime LatestCompletionAt { get; private set; }

        public static SessionSnapshot Unavailable()
        {
            return new SessionSnapshot(false, false, DateTime.MinValue);
        }
    }

    internal sealed class SessionFileSnapshot
    {
        public SessionFileSnapshot(long length, DateTime lastWriteTimeUtc, DateTime latestUserAt, DateTime latestCompletionAt, DateTime latestEventAt)
        {
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
            LatestUserAt = latestUserAt;
            LatestCompletionAt = latestCompletionAt;
            LatestEventAt = latestEventAt;
        }

        public long Length { get; private set; }
        public DateTime LastWriteTimeUtc { get; private set; }
        public DateTime LatestUserAt { get; private set; }
        public DateTime LatestCompletionAt { get; private set; }
        public DateTime LatestEventAt { get; private set; }
    }

    internal sealed class ForegroundSnapshot
    {
        public ForegroundSnapshot(string processName, bool isExternalApp)
        {
            ProcessName = String.IsNullOrWhiteSpace(processName) ? "external app" : processName;
            IsExternalApp = isExternalApp;
        }

        public string ProcessName { get; private set; }
        public bool IsExternalApp { get; private set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public static readonly IntPtr HwndTopMost = new IntPtr(-1);
        public const UInt32 SwpNoActivate = 0x0010;
        public const UInt32 SwpShowWindow = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, UInt32 flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    internal static class T
    {
        public const string AppTitle = "Codex \u5de5\u4f5c\u72b6\u6001\u706f";
        public const string AutoDetect = "\u81ea\u52a8\u68c0\u6d4b";
        public const string SnapToCodex = "\u5438\u9644\u5230 Codex";
        public const string RedWaiting = "\u7ea2\u706f\uff1a\u7b49\u5f85";
        public const string YellowWorking = "\u9ec4\u706f\uff1a\u5de5\u4f5c";
        public const string GreenDone = "\u7eff\u706f\uff1a\u5b8c\u6210";
        public const string Exit = "\u9000\u51fa";
        public const string Waiting = "\u7b49\u5f85\u4e2d";
        public const string Working = "\u5de5\u4f5c\u4e2d";
        public const string Done = "\u5df2\u5b8c\u6210";
        public const string CodexWaiting = "Codex \u6b63\u5728\u7b49\u5f85";
        public const string CodexWorking = "Codex \u6b63\u5728\u5de5\u4f5c";
        public const string CodexThinking = "Codex \u6b63\u5728\u601d\u8003";
        public const string SessionWorking = "Codex \u6b63\u5728\u5de5\u4f5c\uff08\u4f1a\u8bdd\u6267\u884c\u4e2d\uff09";
        public const string OperatingComputer = "Codex \u6b63\u5728\u64cd\u4f5c\u7535\u8111";
        public const string OperatingComputerFormat = "Codex \u6b63\u5728\u64cd\u4f5c\u7535\u8111\uff1a{0}";
        public const string WorkJustFinished = "Codex \u5de5\u4f5c\u521a\u521a\u7ed3\u675f";
        public const string CpuWorkingFormat = "Codex \u6b63\u5728\u5de5\u4f5c\uff0cCPU {0:0.0}%";
        public const string HybridWorkingFormat = "Codex \u6b63\u5728\u5de5\u4f5c\uff0cCPU {0:0.0}% / app {1:0.0}% / base {2:0.0}/{3:0.0}";
        public const string ManualWaiting = "\u624b\u52a8\u8bbe\u4e3a\u7b49\u5f85";
        public const string ManualWorking = "\u624b\u52a8\u8bbe\u4e3a\u5de5\u4f5c\u4e2d";
        public const string ManualDone = "\u624b\u52a8\u8bbe\u4e3a\u5df2\u5b8c\u6210";
    }

    internal static class Logger
    {
        private static readonly string PathValue = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop-light.log");

        public static void Write(string message)
        {
            try
            {
                File.AppendAllText(PathValue, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
