using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _idleIcon;
    private readonly Icon _runningIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startTimerItem;
    private readonly ToolStripMenuItem _stopTimerItem;
    private readonly ToolStripMenuItem _remoteTimersItem;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private readonly Action _singleClick;
    private readonly Action _open;
    private readonly Action _startUnassigned;
    private readonly Action<Guid> _start;
    private bool _disposed;

    public TrayIconService(
        Action singleClick,
        Action open,
        Action startUnassigned,
        Action<Guid> start,
        Action stop,
        Action exit)
    {
        _singleClick = singleClick;
        _open = open;
        _startUnassigned = startUnassigned;
        _start = start;
        _idleIcon = CreateIcon(Color.FromArgb(112, 112, 112), running: false);
        _runningIcon = CreateIcon(Color.FromArgb(64, 201, 119), running: true);
        _singleClickTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, SystemInformation.DoubleClickTime),
        };
        _singleClickTimer.Tick += SingleClickTimer_Tick;

        _menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            ShowImageMargin = false,
            Padding = new Padding(6),
            Renderer = new ToolStripProfessionalRenderer(new CodexColorTable()),
        };
        _menu.Items.Add("Open", null, (_, _) => open());
        _menu.Items.Add("Start timer", null, (_, _) => _startUnassigned());
        _startTimerItem = new ToolStripMenuItem("Start for project");
        ConfigureProjectMenu();
        _menu.Items.Add(_startTimerItem);
        _stopTimerItem = new ToolStripMenuItem("Stop timer", null, (_, _) => stop())
        {
            Enabled = false,
        };
        _menu.Items.Add(_stopTimerItem);
        _remoteTimersItem = new ToolStripMenuItem("Other computers")
        {
            Enabled = false,
            Visible = false,
        };
        _menu.Items.Add(_remoteTimersItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => exit());
        SetProjects([]);

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "Log O'clock — idle",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                QueueSingleClick();
            }
        };
        _notifyIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                OpenFromDoubleClick();
            }
        };
    }

    public void Update(bool running, string tooltip)
    {
        _notifyIcon.Icon = running ? _runningIcon : _idleIcon;
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        _stopTimerItem.Enabled = running;
    }

    public void SetProjects(IReadOnlyList<ProjectOption> projects)
    {
        foreach (var item in _startTimerItem.DropDownItems.Cast<ToolStripItem>().ToArray())
        {
            item.Dispose();
        }

        _startTimerItem.DropDownItems.Clear();
        foreach (var project in projects
                     .OrderBy(option => option.ProjectName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(option => option.ClientName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ToolStripMenuItem($"{project.ProjectName}  ·  {project.ClientName}")
            {
                Tag = project.ProjectId,
                ToolTipText = $"Start tracking {project.ProjectName}",
            };
            item.Click += (_, _) => _start(project.ProjectId);
            _startTimerItem.DropDownItems.Add(item);
        }

        if (_startTimerItem.DropDownItems.Count == 0)
        {
            _startTimerItem.DropDownItems.Add(new ToolStripMenuItem("No projects available")
            {
                Enabled = false,
            });
        }
    }

    public void SetRemoteTimers(IReadOnlyList<RemoteTimerStatus> timers)
    {
        foreach (var item in _remoteTimersItem.DropDownItems.Cast<ToolStripItem>().ToArray())
        {
            item.Dispose();
        }
        _remoteTimersItem.DropDownItems.Clear();
        _remoteTimersItem.Visible = timers.Count > 0;
        _remoteTimersItem.Enabled = timers.Count > 0;
        foreach (var timer in timers)
        {
            var work = string.Join(
                " · ",
                new[] { timer.TaskName, timer.ProjectName }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            var started = timer.StartedUtc?.ToLocalTime().ToString("t") ?? "unknown";
            _remoteTimersItem.DropDownItems.Add(new ToolStripMenuItem(
                $"{timer.DeviceName}: {(string.IsNullOrWhiteSpace(work) ? "tracking" : work)} since {started}")
            {
                Enabled = false,
                ToolTipText = "Read-only timer status from another synchronized computer",
            });
        }
    }

    internal bool StartProjectForPreview(Guid projectId)
    {
        var item = _startTimerItem.DropDownItems
            .OfType<ToolStripMenuItem>()
            .FirstOrDefault(candidate => candidate.Tag is Guid id && id == projectId);
        item?.PerformClick();
        return item is not null;
    }

    internal void StartUnassignedForPreview() => _startUnassigned();

    internal void SingleLeftClickForPreview() => QueueSingleClick();

    internal void DoubleLeftClickForPreview() => OpenFromDoubleClick();

    internal string CurrentTooltipForPreview => _notifyIcon.Text;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _singleClickTimer.Stop();
        _singleClickTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _idleIcon.Dispose();
        _runningIcon.Dispose();
    }

    private void QueueSingleClick()
    {
        _singleClickTimer.Stop();
        _singleClickTimer.Start();
    }

    private void OpenFromDoubleClick()
    {
        _singleClickTimer.Stop();
        _open();
    }

    private void SingleClickTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _singleClickTimer.Stop();
        _singleClick();
    }

    private void ConfigureProjectMenu()
    {
        if (_startTimerItem.DropDown is ToolStripDropDownMenu projectMenu)
        {
            projectMenu.BackColor = Color.FromArgb(40, 40, 40);
            projectMenu.ForeColor = Color.White;
            projectMenu.Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            projectMenu.ShowImageMargin = false;
            projectMenu.Padding = new Padding(6);
            projectMenu.Renderer = new ToolStripProfessionalRenderer(new CodexColorTable());
        }
    }

    private static Icon CreateIcon(Color accent, bool running)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(accent);
        graphics.FillEllipse(brush, 2, 2, 28, 28);
        using var inner = new SolidBrush(Color.White);
        if (running)
        {
            graphics.FillRectangle(inner, 11, 9, 4, 14);
            graphics.FillRectangle(inner, 18, 9, 4, 14);
        }
        else
        {
            graphics.FillPolygon(inner, [new Point(12, 8), new Point(23, 16), new Point(12, 24)]);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    private sealed class CodexColorTable : ProfessionalColorTable
    {
        private static readonly Color Elevated = Color.FromArgb(40, 40, 40);
        private static readonly Color Hover = Color.FromArgb(52, 52, 52);
        private static readonly Color Border = Color.FromArgb(66, 66, 66);

        public override Color ToolStripDropDownBackground => Elevated;
        public override Color ImageMarginGradientBegin => Elevated;
        public override Color ImageMarginGradientMiddle => Elevated;
        public override Color ImageMarginGradientEnd => Elevated;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
