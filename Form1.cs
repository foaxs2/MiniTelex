using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace MiniTelex;

public partial class Form1 : Form
{
    private readonly string _configPath;
    private readonly AppConfig _config;
    private readonly GameContextDetector _detector;
    private readonly InputHookManager _hookManager;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly NotifyIcon _trayIcon;

    private Button _btnToggleMaster = null!;
    private CheckBox _chkStartupWindows = null!;
    private bool _allowRealExit;
    private bool _hasShownTrayTip;
    private EventWaitHandle? _showWindowEvent;

    public Form1()
    {
        InitializeComponent();

        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        _config = GameContextDetector.LoadConfig(_configPath);

        // Mặc định bật toàn bộ cơ chế thông minh ngầm
        _config.AutoCloseUnikey = true;
        _config.EnterTogglesChat = true;
        _config.MouseClickChatEnabled = true;
        _config.ShowFloatingHud = false;

        _detector = new GameContextDetector(_config, _configPath);
        _hookManager = new InputHookManager(_detector, _config);

        BuildMinimalUi();

        _trayIcon = CreateTrayIcon();

        GameContextDetector.SetStartupWithWindows(_config.StartWithWindows);
        GameContextDetector.CloseConflictingImes();

        _hookManager.StateChanged += () =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(RefreshUiState));
            }
        };

        _hookManager.Start();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _pollTimer.Tick += (_, _) =>
        {
            if (GameContextDetector.IsConflictingImeRunning(out _))
            {
                GameContextDetector.CloseConflictingImes();
            }
            _hookManager.SyncWindowContext();
        };
        _pollTimer.Start();

        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\MiniTelex_ShowWindow_Event");
            ThreadPool.RegisterWaitForSingleObject(_showWindowEvent, (_, _) =>
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(RestoreMainWindow));
                }
            }, null, -1, false);
        }
        catch
        {
            // Ignore event handle error
        }

        RefreshUiState();
    }

    private void BuildMinimalUi()
    {
        BackColor = Color.FromArgb(18, 24, 38);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // Nút chính Bật / Tắt (Ctrl + Shift)
        _btnToggleMaster = new Button
        {
            Location = new Point(16, 14),
            Size = new Size(278, 48),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnToggleMaster.FlatAppearance.BorderSize = 0;
        _btnToggleMaster.Click += (_, _) =>
        {
            _config.Enabled = !_config.Enabled;
            _detector.SaveConfig();
            RefreshUiState();
        };

        // Tuỳ chọn Khởi động cùng Windows
        _chkStartupWindows = new CheckBox
        {
            Location = new Point(18, 72),
            Size = new Size(274, 24),
            Checked = _config.StartWithWindows,
            ForeColor = Color.FromArgb(203, 213, 225),
            Text = "Tự bật khi mở máy tính (Windows)"
        };
        _chkStartupWindows.CheckedChanged += (_, _) =>
        {
            _config.StartWithWindows = _chkStartupWindows.Checked;
            GameContextDetector.SetStartupWithWindows(_config.StartWithWindows);
            _detector.SaveConfig();
        };

        // Hàng nút chức năng nhỏ gọn phía dưới (giống Unikey: Đóng chạy ngầm / Thoát hẳn)
        var btnHide = new Button
        {
            Location = new Point(16, 106),
            Size = new Size(134, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand,
            Text = "Đóng (Chạy ngầm)"
        };
        btnHide.FlatAppearance.BorderSize = 0;
        btnHide.Click += (_, _) => Close();

        var btnExit = new Button
        {
            Location = new Point(160, 106),
            Size = new Size(134, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = Color.FromArgb(252, 165, 165),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand,
            Text = "Thoát hẳn"
        };
        btnExit.FlatAppearance.BorderSize = 0;
        btnExit.Click += (_, _) =>
        {
            _allowRealExit = true;
            _trayIcon.Visible = false;
            Application.Exit();
        };

        Controls.Add(_btnToggleMaster);
        Controls.Add(_chkStartupWindows);
        Controls.Add(btnHide);
        Controls.Add(btnExit);
    }

    private void RefreshUiState()
    {
        if (!_config.Enabled)
        {
            _btnToggleMaster.BackColor = Color.FromArgb(71, 85, 105);
            _btnToggleMaster.ForeColor = Color.White;
            _btnToggleMaster.Text = "⚪ ĐANG TẮT (Ctrl + Shift)";
        }
        else
        {
            _btnToggleMaster.BackColor = Color.FromArgb(22, 163, 74);
            _btnToggleMaster.ForeColor = Color.White;
            _btnToggleMaster.Text = "🟢 TIẾNG VIỆT (Ctrl + Shift)";
        }
    }

    public void RestoreMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mở MiniTelex", null, (_, _) => RestoreMainWindow());
        menu.Items.Add("Bật / Tắt (Ctrl + Shift)", null, (_, _) =>
        {
            _config.Enabled = !_config.Enabled;
            RefreshUiState();
        });
        if (!IsRunningAsAdministrator())
        {
            menu.Items.Add("Chạy quyền Admin (Cho Genshin)", null, (_, _) => RestartAsAdmin());
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Thoát hẳn", null, (_, _) =>
        {
            _allowRealExit = true;
            _trayIcon.Visible = false;
            Application.Exit();
        });

        var appIcon = LoadAppIcon();
        Icon = appIcon;

        var icon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "MiniTelex (Ctrl + Shift)",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => RestoreMainWindow();
        return icon;
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.ico");
            if (File.Exists(icoPath))
            {
                return new Icon(icoPath);
            }

            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            var extracted = Icon.ExtractAssociatedIcon(exePath);
            if (extracted != null)
            {
                return extracted;
            }
        }
        catch
        {
            // Fallback
        }
        return SystemIcons.Shield;
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void RestartAsAdmin()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            _allowRealExit = true;
            _trayIcon.Visible = false;
            Application.Exit();
        }
        catch
        {
            // Ignore UAC cancel
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_allowRealExit)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();

            if (!_hasShownTrayTip)
            {
                _hasShownTrayTip = true;
                _trayIcon.ShowBalloonTip(
                    2000,
                    "MiniTelex đang chạy ngầm",
                    "Nháy đúp biểu tượng dưới góc phải để mở lại hoặc bấm Ctrl+Shift để Bật/Tắt.",
                    ToolTipIcon.Info);
            }
            return;
        }

        _detector.SaveConfig();
        _pollTimer.Stop();
        _hookManager.Dispose();
        _trayIcon.Dispose();
        _showWindowEvent?.Dispose();
        base.OnFormClosing(e);
    }
}
