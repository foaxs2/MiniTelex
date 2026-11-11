using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MiniTelex;

public enum ChatTriggerSource
{
    None,
    EnterKey,
    MouseClick
}

public sealed class InputHookManager : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private const uint LLKHF_INJECTED = 0x00000010;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const int VK_BACK = 0x08;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_TAB = 0x09;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_CAPITAL = 0x14; // CapsLock
    private const int CURSOR_SHOWING = 0x00000001;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static readonly IntPtr MiniTelexSignature = new IntPtr(0x4D544C58); // 'MTLX'

    // Các tựa game FPS mở chat bằng phím Enter (không dùng click chuột để mở chat nhằm bảo vệ 100% thao tác bắn súng + strafe WASD)
    private static readonly HashSet<string> FpsEnterOnlyGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "valorant-win64-shipping",
        "valorant",
        "cs2",
        "csgo",
        "r5apex",
        "overwatch",
        "tslgame",
        "pubg",
        "deadlock",
        "deltaforceclient-win64-shipping",
        "crossfire"
    };

    private readonly TelexEngine _engine = new();
    private readonly GameContextDetector _detector;
    private readonly AppConfig _config;

    private readonly LowLevelProc _kbProc;
    private readonly LowLevelProc _mouseProc;
    private IntPtr _kbHookId = IntPtr.Zero;
    private IntPtr _mouseHookId = IntPtr.Zero;

    // Theo dõi trạng thái nhấn giữ phím (chống liệt phím WASD tuyệt đối)
    private readonly HashSet<uint> _currentlyDownKeys = new();
    private readonly Dictionary<uint, long> _keyDownTimestamps = new();
    private long _lastLetterTapTimestamp;
    private bool _previousKeyWasHeldOrMultiKey;

    // Trạng thái Chat trong Game
    private ChatTriggerSource _chatTrigger = ChatTriggerSource.None;
    private bool _armedByMouseClick;
    private POINT _lastClickPoint;
    private long _lastMouseClickTimestamp;
    private int _consecutiveCleanTapsAfterClick;
    private bool _ctrlShiftArmed;
    private long _ctrlShiftArmedTimestamp;

    public OperatingMode CurrentMode { get; private set; } = OperatingMode.DesktopTelex;
    public ChatTriggerSource ActiveChatTrigger => _chatTrigger;

    public event Action? StateChanged;

    public InputHookManager(GameContextDetector detector, AppConfig config)
    {
        _detector = detector;
        _config = config;
        _kbProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Start()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        IntPtr hMod = GetModuleHandle(curModule.ModuleName);

        _kbHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        SyncWindowContext();
    }

    public void SyncWindowContext()
    {
        bool windowChanged = _detector.RefreshActiveWindow();
        if (!_detector.IsCurrentWindowGame)
        {
            if (CurrentMode != OperatingMode.DesktopTelex || windowChanged)
            {
                CurrentMode = OperatingMode.DesktopTelex;
                _chatTrigger = ChatTriggerSource.None;
                _armedByMouseClick = false;
                _engine.Reset();
                StateChanged?.Invoke();
            }
        }
        else
        {
            if (windowChanged)
            {
                CurrentMode = OperatingMode.InGameCombat;
                _chatTrigger = ChatTriggerSource.None;
                _armedByMouseClick = false;
                _engine.Reset();
                StateChanged?.Invoke();
            }
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _config.Enabled)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                OnMouseClicked(msg, ms.pt);
            }
        }
        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private void OnMouseClicked(int mouseMessage, POINT clickPt)
    {
        _ctrlShiftArmed = false;
        _detector.RefreshActiveWindow();
        _engine.Reset();

        if (!_detector.IsCurrentWindowGame)
        {
            if (CurrentMode != OperatingMode.DesktopTelex)
            {
                CurrentMode = OperatingMode.DesktopTelex;
                _chatTrigger = ChatTriggerSource.None;
                StateChanged?.Invoke();
            }
            return;
        }

        long now = Environment.TickCount64;
        _lastMouseClickTimestamp = now;

        // 1. Click chuột phải hoặc chuột giữa -> Luôn thoát Chat về Combat ngay lập tức!
        if (mouseMessage == WM_RBUTTONDOWN || mouseMessage == WM_MBUTTONDOWN)
        {
            _armedByMouseClick = false;
            _consecutiveCleanTapsAfterClick = 0;
            if (CurrentMode != OperatingMode.InGameCombat)
            {
                CurrentMode = OperatingMode.InGameCombat;
                _chatTrigger = ChatTriggerSource.None;
                StateChanged?.Invoke();
            }
            return;
        }

        // 2. Click chuột trái trong Game:
        if (mouseMessage == WM_LBUTTONDOWN)
        {
            bool wasInChat = CurrentMode == OperatingMode.InGameChat;
            CurrentMode = OperatingMode.InGameCombat;
            _chatTrigger = ChatTriggerSource.None;
            _consecutiveCleanTapsAfterClick = 0;

            // QUAN TRỌNG:
            // - Nếu con trỏ chuột đang bị ẨN (đang cầm tâm bắn súng trong Valorant, CS2, Genshin, PUBG...)
            //   hoặc đang trong game FPS chuyên dùng phím Enter (Valorant, CS2),
            //   thì Click Chuột Trái chắc chắn 100% là BẮN SÚNG / ĐÁNH THƯỜNG, TUYỆT ĐỐI KHÔNG bật _armedByMouseClick!
            if (!IsMouseCursorVisible() || FpsEnterOnlyGames.Contains(_detector.CurrentProcessName))
            {
                _armedByMouseClick = false;
            }
            else
            {
                // Chỉ khi con trỏ chuột đang HIỂN THỊ trên màn hình (như trong TFT, LMHT, hoặc khi bật chuột click vào ô chat)
                _armedByMouseClick = true;
                _lastClickPoint = clickPt;
            }

            if (wasInChat)
            {
                StateChanged?.Invoke();
            }
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);

        var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // Bỏ qua các phím do chính MiniTelex phát ra
        if (kb.dwExtraInfo == MiniTelexSignature || (kb.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        uint vk = kb.vkCode;
        long now = Environment.TickCount64;

        // Xử lý khi nhả phím (KEYUP)
        if (msg is WM_KEYUP or WM_SYSKEYUP)
        {
            if (_ctrlShiftArmed && (IsCtrlVk(vk) || IsShiftVk(vk)))
            {
                _ctrlShiftArmed = false;
                if (now - _ctrlShiftArmedTimestamp <= 750)
                {
                    _config.Enabled = !_config.Enabled;
                    _engine.Reset();
                    StateChanged?.Invoke();
                }
            }

            if (_keyDownTimestamps.TryGetValue(vk, out long downTime))
            {
                long holdDuration = now - downTime;
                if (_detector.IsCurrentWindowGame && holdDuration > _config.KeyHoldThresholdMs && IsLetterVk(vk))
                {
                    _previousKeyWasHeldOrMultiKey = true;
                    _engine.Reset();
                }
                _keyDownTimestamps.Remove(vk);
            }
            _currentlyDownKeys.Remove(vk);
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        if (msg is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);

        // Tổ hợp phím tắt Ctrl + Shift để Bật/Tắt nhanh MiniTelex
        if (IsCtrlVk(vk) || IsShiftVk(vk))
        {
            bool ctrlDown = IsCtrlVk(vk) || IsKeyDown(VK_CONTROL);
            bool shiftDown = IsShiftVk(vk) || IsKeyDown(VK_SHIFT);
            bool altDown = IsKeyDown(VK_MENU);

            if (ctrlDown && shiftDown && !altDown && !_currentlyDownKeys.Contains(vk))
            {
                _ctrlShiftArmed = true;
                _ctrlShiftArmedTimestamp = now;
            }
            _currentlyDownKeys.Add(vk);
        }
        else
        {
            _ctrlShiftArmed = false;
        }

        // Phím tắt dự phòng Alt + Z
        if (vk == (uint)Keys.Z && IsKeyDown(VK_MENU) && !IsKeyDown(VK_CONTROL))
        {
            _config.Enabled = !_config.Enabled;
            _engine.Reset();
            StateChanged?.Invoke();
            return (IntPtr)1;
        }

        if (!_config.Enabled)
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);

        if (vk == VK_TAB && IsKeyDown(VK_MENU))
        {
            _engine.Reset();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        if (IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU) || IsCtrlVk(vk))
        {
            _engine.Reset();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        if (IsShiftVk(vk) || vk == VK_CAPITAL)
        {
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        SyncWindowContext();

        bool isKeyRepeat = _currentlyDownKeys.Contains(vk);
        if (!isKeyRepeat)
        {
            _currentlyDownKeys.Add(vk);
            _keyDownTimestamps[vk] = now;
        }

        // =========================================================================
        // CHẾ ĐỘ 1: NGOÀI DESKTOP / CHROME / ZALO / DISCORD (Khi Alt + Tab ra ngoài)
        // =========================================================================
        if (!_detector.IsCurrentWindowGame)
        {
            return ProcessStandardTelexKey(nCode, wParam, lParam, vk, allowStandaloneW: true);
        }

        // =========================================================================
        // CHẾ ĐỘ 2 & 3: ĐANG TRONG CỬA SỔ GAME (LMHT, TFT, Valorant, CS2, Genshin...)
        // =========================================================================

        // 1. Phím ESC trong Game -> Luôn thoát Chat về Combat ngay lập tức
        if (vk == VK_ESCAPE)
        {
            ExitGameChatToCombat();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        // 2. Phím ENTER trong Game -> Bật / Tắt Chat
        if (vk == VK_RETURN)
        {
            _engine.Reset();
            _armedByMouseClick = false;
            _consecutiveCleanTapsAfterClick = 0;

            if (CurrentMode == OperatingMode.InGameCombat)
            {
                CurrentMode = OperatingMode.InGameChat;
                _chatTrigger = ChatTriggerSource.EnterKey;
                _lastLetterTapTimestamp = now;
            }
            else
            {
                CurrentMode = OperatingMode.InGameCombat;
                _chatTrigger = ChatTriggerSource.None;
            }
            StateChanged?.Invoke();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        // 3. CHỐNG LIỆT PHÍM TUYỆT ĐỐI KHI NHẤN GIỮ (KEY REPEAT) HOẶC ĐÈ NHIỀU PHÍM DI CHUYỂN CÙNG LÚC
        int heldLetterCount = CountCurrentlyDownLetterKeys();
        if (isKeyRepeat && IsLetterVk(vk))
        {
            _previousKeyWasHeldOrMultiKey = true;
            // Ngay cả khi đang ở trong Chat (do quên tắt chat hoặc bấm nhầm), hễ đè giữ phím chữ cái (WASD) là thoát Chat ngay!
            ExitGameChatToCombat();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        // Nếu đè 2 phím di chuyển WASD cùng lúc (vd W+A, W+D, A+D, S+A, S+D) -> Chắc chắn 100% là đang di chuyển trong game!
        if (heldLetterCount >= 2 && IsWasdVk(vk))
        {
            _previousKeyWasHeldOrMultiKey = true;
            ExitGameChatToCombat();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        // 4. Nếu đang ở chế độ IN-GAME CHAT:
        if (CurrentMode == OperatingMode.InGameChat)
        {
            // Nếu mở chat nhưng quá 10 giây không gõ chữ nào và người dùng bấm phím WASD khi con trỏ chuột đang ẩn (đang chạy trong Valorant/CS2)
            // -> Tự động thoát Chat để không bao giờ bị liệt phím!
            if (now - _lastLetterTapTimestamp > 10000 && IsWasdVk(vk) && !IsMouseCursorVisible())
            {
                ExitGameChatToCombat();
                return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
            }

            _lastLetterTapTimestamp = now;
            bool allowStandaloneW = _chatTrigger == ChatTriggerSource.EnterKey;
            return ProcessStandardTelexKey(nCode, wParam, lParam, vk, allowStandaloneW);
        }

        // 5. Nếu đang ở chế độ IN-GAME COMBAT nhưng vừa Click chuột trái khi con trỏ hiển thị (TFT, LMHT, Genshin...)
        if (_armedByMouseClick)
        {
            // Kiểm tra xem sau khi click chuột, con trỏ chuột có bị ẩn đi hoặc di chuyển đi chỗ khác (> 50px) không:
            // Nếu người chơi vừa click vừa lia chuột đi chỗ khác -> Đó là thao tác chơi game, KHÔNG phải click vào ô chat!
            if (!IsMouseCursorVisible() || HasMouseMovedSinceClick(50))
            {
                _armedByMouseClick = false;
                _engine.Reset();
                return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
            }

            if (vk == VK_BACK)
            {
                _engine.HandleBackspace();
                return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
            }

            if (vk == VK_SPACE)
            {
                if (_consecutiveCleanTapsAfterClick >= 2 && _engine.BufferLength >= 2 && !_engine.IsPureWasdWord())
                {
                    CurrentMode = OperatingMode.InGameChat;
                    _chatTrigger = ChatTriggerSource.MouseClick;
                    StateChanged?.Invoke();
                }
                _engine.HandleSpace();
                return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
            }

            if (IsLetterVk(vk))
            {
                long gapSinceLastTap = now - _lastLetterTapTimestamp;
                if (_previousKeyWasHeldOrMultiKey || (_engine.BufferLength > 0 && gapSinceLastTap > _config.TypingCadenceMaxMs))
                {
                    _engine.Reset();
                    _consecutiveCleanTapsAfterClick = 0;
                    _previousKeyWasHeldOrMultiKey = false;
                }

                _lastLetterTapTimestamp = now;
                _consecutiveCleanTapsAfterClick++;

                char inputChar = VkToChar(vk);
                if (_engine.ProcessKey(inputChar, allowStandaloneW: false, out int backspaces, out string replacement))
                {
                    // BẢO VỆ TUYỆT ĐỐI CHO WASD:
                    // Nếu các phím vừa gõ chỉ toàn là W, A, S, D (vd bấm A+S, D+D, A+A khi lách né đòn),
                    // TUYỆT ĐỐI KHÔNG tự bật chế độ Chat bằng Chuột!
                    if (_engine.IsPureWasdWord())
                    {
                        _engine.Reset();
                        return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
                    }

                    CurrentMode = OperatingMode.InGameChat;
                    _chatTrigger = ChatTriggerSource.MouseClick;
                    SendReplacement(backspaces, replacement);
                    StateChanged?.Invoke();
                    return (IntPtr)1;
                }

                return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
            }

            _engine.Reset();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        // Mặc định trong Combat: Cho mọi phím đi thẳng 100% vào Game với độ trễ 0ms!
        _engine.Reset();
        return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
    }

    private void ExitGameChatToCombat()
    {
        _engine.Reset();
        _armedByMouseClick = false;
        _consecutiveCleanTapsAfterClick = 0;
        if (CurrentMode != OperatingMode.InGameCombat)
        {
            CurrentMode = OperatingMode.InGameCombat;
            _chatTrigger = ChatTriggerSource.None;
            StateChanged?.Invoke();
        }
    }

    private static bool IsMouseCursorVisible()
    {
        try
        {
            var pci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref pci))
            {
                return (pci.flags & CURSOR_SHOWING) != 0;
            }
        }
        catch
        {
            // Ignore
        }
        return true;
    }

    private bool HasMouseMovedSinceClick(int thresholdPx)
    {
        try
        {
            if (GetCursorPos(out POINT pt))
            {
                int dx = pt.x - _lastClickPoint.x;
                int dy = pt.y - _lastClickPoint.y;
                return (dx * dx + dy * dy) > (thresholdPx * thresholdPx);
            }
        }
        catch
        {
            // Ignore
        }
        return false;
    }

    private IntPtr ProcessStandardTelexKey(int nCode, IntPtr wParam, IntPtr lParam, uint vk, bool allowStandaloneW)
    {
        if (vk == VK_BACK)
        {
            _engine.HandleBackspace();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        if (vk == VK_SPACE)
        {
            _engine.HandleSpace();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        if (vk == VK_RETURN || vk == VK_TAB)
        {
            _engine.Reset();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        if (!IsLetterVk(vk))
        {
            _engine.Reset();
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        char inputChar = VkToChar(vk);
        if (_engine.ProcessKey(inputChar, allowStandaloneW, out int backspaces, out string replacement))
        {
            SendReplacement(backspaces, replacement);
            return (IntPtr)1;
        }

        return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
    }

    private static void SendReplacement(int backspaces, string replacement)
    {
        int totalEvents = (backspaces * 2) + (replacement.Length * 2);
        if (totalEvents == 0)
            return;

        var inputs = new INPUT[totalEvents];
        int idx = 0;

        for (int i = 0; i < backspaces; i++)
        {
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_BACK,
                        wScan = 0,
                        dwFlags = 0,
                        dwExtraInfo = MiniTelexSignature
                    }
                }
            };
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_BACK,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        dwExtraInfo = MiniTelexSignature
                    }
                }
            };
        }

        foreach (char c in replacement)
        {
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        dwExtraInfo = MiniTelexSignature
                    }
                }
            };
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        dwExtraInfo = MiniTelexSignature
                    }
                }
            };
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static int GetInputStructSize() => Marshal.SizeOf<INPUT>();

    private int CountCurrentlyDownLetterKeys()
    {
        int count = 0;
        foreach (uint k in _currentlyDownKeys)
        {
            if (IsLetterVk(k))
                count++;
        }
        return count;
    }

    private static bool IsLetterVk(uint vk) => vk >= (uint)Keys.A && vk <= (uint)Keys.Z;

    private static bool IsWasdVk(uint vk) =>
        vk == (uint)Keys.W || vk == (uint)Keys.A || vk == (uint)Keys.S || vk == (uint)Keys.D;

    private static bool IsCtrlVk(uint vk) => vk is VK_CONTROL or 0xA2 or 0xA3;

    private static bool IsShiftVk(uint vk) => vk is VK_SHIFT or 0xA0 or 0xA1;

    private static bool IsKeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    private static bool IsCapsLockToggled() => (GetKeyState(VK_CAPITAL) & 0x0001) != 0;

    private static char VkToChar(uint vk)
    {
        bool shift = IsKeyDown(VK_SHIFT);
        bool caps = IsCapsLockToggled();
        bool upper = shift ^ caps;
        char baseChar = (char)('a' + (vk - (uint)Keys.A));
        return upper ? char.ToUpperInvariant(baseChar) : baseChar;
    }

    public void Dispose()
    {
        if (_kbHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_kbHookId);
            _kbHookId = IntPtr.Zero;
        }
        if (_mouseHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
    }
}
