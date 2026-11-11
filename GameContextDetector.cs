using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MiniTelex;

public enum OperatingMode
{
    /// <summary>
    /// Ngoài màn hình Desktop, trình duyệt (Chrome, Edge, Cốc Cốc), Zalo, Discord... (Khi Alt + Tab ra ngoài)
    /// -> Bật 100% Telex chuẩn như Unikey.
    /// </summary>
    DesktopTelex,

    /// <summary>
    /// Đang trong cửa sổ Game và điều khiển nhân vật (WASD, QWER, bắn súng, di chuyển)
    /// -> Bảo vệ tuyệt đối phím di chuyển/chiêu thức, không bao giờ gây khựng hay liệt nút.
    /// </summary>
    InGameCombat,

    /// <summary>
    /// Đang mở khung Chat trong Game (bằng phím Enter hoặc Click chuột vào ô chat)
    /// -> Bật Telex thông minh trong game.
    /// </summary>
    InGameChat
}

public sealed class AppConfig
{
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool AutoCloseUnikey { get; set; } = true;
    public bool ShowFloatingHud { get; set; } = true;
    public bool EnterTogglesChat { get; set; } = true;
    public bool MouseClickChatEnabled { get; set; } = true;
    public int KeyHoldThresholdMs { get; set; } = 125;
    public int TypingCadenceMaxMs { get; set; } = 480;
    public List<string> CustomGameProcesses { get; set; } = new();
    public List<string> CustomDesktopProcesses { get; set; } = new();
}

public sealed class GameContextDetector
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // Danh sách các ứng dụng Desktop/Chat/Trình duyệt phổ biến (luôn bật Full Telex khi Alt+Tab sang)
    private static readonly HashSet<string> KnownDesktopApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "brave", "coccoc", "opera", "vivaldi",
        "zalo", "zalocall", "discord", "telegram", "messenger", "skype", "slack", "teams",
        "notepad", "wordpad", "winword", "excel", "powerpnt", "onenote", "outlook",
        "code", "devenv", "rider64", "idea64", "sublime_text", "notepad++",
        "cmd", "powershell", "pwsh", "windowsterminal", "taskmgr", "control",
        "spotify", "vlc", "potplayermini64", "obs64", "steamwebhelper", "epicgameslauncher",
        "riotclientux", "leagueclientux", "leagueclient", "minitelex"
    };

    // Danh sách các tựa game phổ biến (LMHT, TFT, Valorant, CS2, Genshin, PUBG, Dota 2, Naraka, GTA V...)
    private static readonly HashSet<string> KnownGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "league of legends",           // Liên Minh Huyền Thoại & Đấu Trường Chân Lý (TFT trong trận)
        "valorant-win64-shipping",     // Valorant
        "valorant",
        "cs2",                         // Counter-Strike 2
        "csgo",
        "genshinimpact",               // Genshin Impact (Quốc tế)
        "yuanshen",                    // Genshin Impact (TQ)
        "starrail",                    // Honkai: Star Rail
        "zenlesszonezero",             // Zenless Zone Zero
        "client-win64-shipping",       // Wuthering Waves / Unreal Games
        "dota2",                       // Dota 2
        "tslgame",                     // PUBG
        "narakabladePoint",            // Naraka: Bladepoint
        "eldenring",                   // Elden Ring
        "b1-win64-shipping",           // Black Myth: Wukong
        "overwatch",                   // Overwatch 2
        "r5apex",                      // Apex Legends
        "robloxplayerbeta",            // Roblox
        "javaw",                       // Minecraft Java
        "minecraft.windows",           // Minecraft Bedrock
        "gta5",                        // GTA V
        "fivem",                       // FiveM
        "deadlock",                    // Deadlock
        "palworld-win64-shipping",     // Palworld
        "oncehuman",                   // Once Human
        "deltaforceclient-win64-shipping", // Delta Force
        "crossfire",                   // Đột Kích
        "fconline",                    // FC Online (FIFA)
        "fc24",
        "fc25",
        "lostark",
        "pathofexile",
        "pathofexile_x64",
        "diablo iv",
        "witcher3",
        "cyberpunk2077",
        "helldivers2",
        "marvelrivals-win64-shipping"
    };

    private readonly AppConfig _config;
    private readonly string _configPath;

    public string CurrentProcessName { get; private set; } = "unknown";
    public string CurrentWindowTitle { get; private set; } = "";
    public bool IsCurrentWindowGame { get; private set; }
    public IntPtr CurrentWindowHandle { get; private set; }

    public GameContextDetector(AppConfig config, string configPath)
    {
        _config = config;
        _configPath = configPath;
    }

    public static AppConfig LoadConfig(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null)
                    return cfg;
            }
        }
        catch
        {
            // Ignore malformed config and use default
        }
        return new AppConfig();
    }

    public void SaveConfig()
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, opts));
        }
        catch
        {
            // Ignore write error
        }
    }

    /// <summary>
    /// Kiểm tra cửa sổ đang active hiện tại (phát hiện tức thì khi người dùng Alt + Tab).
    /// Trả về true nếu vừa có sự thay đổi cửa sổ hoặc trạng thái Game/Desktop.
    /// </summary>
    public bool RefreshActiveWindow()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        bool changed = hwnd != CurrentWindowHandle;
        CurrentWindowHandle = hwnd;

        GetWindowThreadProcessId(hwnd, out uint pid);
        string procName = GetProcessNameSafe(pid);
        string title = GetWindowTitleSafe(hwnd);

        bool isGame = DetermineIfGame(hwnd, procName, title);

        if (procName != CurrentProcessName || isGame != IsCurrentWindowGame || title != CurrentWindowTitle)
        {
            CurrentProcessName = procName;
            CurrentWindowTitle = title;
            IsCurrentWindowGame = isGame;
            return true;
        }

        return changed;
    }

    private bool DetermineIfGame(IntPtr hwnd, string procName, string title)
    {
        if (string.IsNullOrWhiteSpace(procName))
            return false;

        // 1. Ưu tiên danh sách người dùng tự cấu hình
        foreach (string customDesktop in _config.CustomDesktopProcesses)
        {
            if (string.Equals(customDesktop, procName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (string customGame in _config.CustomGameProcesses)
        {
            if (string.Equals(customGame, procName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 2. Nếu là ứng dụng Desktop/Trình duyệt/Chat quen thuộc (Chrome, Zalo, Discord...) -> Chắc chắn KHÔNG phải game
        if (KnownDesktopApps.Contains(procName))
            return false;

        // 3. Nếu nằm trong danh sách các tựa game đã biết (LMHT, TFT, Valorant, CS2, Genshin...) -> Chắc chắn là Game
        if (KnownGameProcesses.Contains(procName))
            return true;

        // Kiểm tra theo từ khóa trong tên tiến trình (ví dụ các game Unreal Engine thường có đuôi -Win64-Shipping)
        if (procName.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
            procName.Contains("genshin", StringComparison.OrdinalIgnoreCase) ||
            procName.Contains("valorant", StringComparison.OrdinalIgnoreCase) ||
            procName.Contains("league of legends", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 4. Tự động phát hiện mọi tựa game khác ("Gần như game nào cũng chơi"):
        // Nếu cửa sổ đang ở chế độ Fullscreen hoặc Borderless Fullscreen (chiếm toàn bộ màn hình và không có thanh tiêu đề chuẩn của Windows)
        if (IsWindowFullscreenOrBorderless(hwnd))
            return true;

        return false;
    }

    private static bool IsWindowFullscreenOrBorderless(IntPtr hwnd)
    {
        try
        {
            if (!GetWindowRect(hwnd, out RECT winRect))
                return false;

            IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero)
                return false;

            MONITORINFO mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref mi))
                return false;

            int winW = winRect.Right - winRect.Left;
            int winH = winRect.Bottom - winRect.Top;
            int monW = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int monH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

            if (monW <= 0 || monH <= 0)
                return false;

            // Kiểm tra cửa sổ có phủ toàn màn hình không
            bool coversMonitor = winW >= monW && winH >= monH &&
                                 Math.Abs(winRect.Left - mi.rcMonitor.Left) <= 2 &&
                                 Math.Abs(winRect.Top - mi.rcMonitor.Top) <= 2;

            if (!coversMonitor)
                return false;

            int style = GetWindowLong(hwnd, GWL_STYLE);
            bool hasStandardFrame = (style & (WS_CAPTION | WS_THICKFRAME)) == (WS_CAPTION | WS_THICKFRAME);
            return !hasStandardFrame;
        }
        catch
        {
            return false;
        }
    }

    private static string GetProcessNameSafe(uint pid)
    {
        if (pid == 0)
            return "unknown";
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetWindowTitleSafe(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            if (GetWindowText(hwnd, sb, sb.Capacity) > 0)
                return sb.ToString();
        }
        catch
        {
            // Ignore
        }
        return "";
    }

    /// <summary>
    /// Tự động đóng Unikey / EVKey đang chạy ngầm để tránh xung đột hai bộ gõ cùng lúc.
    /// </summary>
    public static int CloseConflictingImes()
    {
        int closedCount = 0;
        string[] conflictingNames = { "UniKeyNT", "UniKey", "EVKey64", "EVKey32" };
        foreach (string name in conflictingNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    try
                    {
                        p.Kill();
                        closedCount++;
                    }
                    catch
                    {
                        // Nếu Unikey đang chạy quyền Admin mà MiniTelex chưa chạy Admin thì bỏ qua
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }
        return closedCount;
    }

    public static bool IsConflictingImeRunning(out string imeName)
    {
        imeName = "";
        string[] conflictingNames = { "UniKeyNT", "UniKey", "EVKey64", "EVKey32" };
        foreach (string name in conflictingNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    imeName = name;
                    return true;
                }
            }
            catch
            {
                // Ignore
            }
        }
        return false;
    }

    public static void SetStartupWithWindows(bool enable)
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appName = "MiniTelex";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                string preferredExe = @"D:\App\MiniTelex\MiniTelex.exe";
                if (!File.Exists(preferredExe))
                {
                    preferredExe = Environment.ProcessPath ?? preferredExe;
                }
                key.SetValue(appName, $"\"{preferredExe}\"");
            }
            else
            {
                key.DeleteValue(appName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore registry errors
        }
    }
}
