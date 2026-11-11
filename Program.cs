using System;
using System.Threading;
using System.Windows.Forms;

namespace MiniTelex;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            return RunSelfTest();
        }

        using var mutex = new Mutex(true, "Global\\MiniTelex_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting("Global\\MiniTelex_ShowWindow_Event");
                evt.Set();
            }
            catch
            {
                // Fallback if event not found
            }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
        return 0;
    }

    private static int RunSelfTest()
    {
        int structSize = InputHookManager.GetInputStructSize();
        int expectedSize = IntPtr.Size == 8 ? 40 : 28;
        if (structSize != expectedSize)
        {
            Console.Error.WriteLine($"FAIL: sizeof(INPUT)={structSize}, expected={expectedSize}");
            return 1;
        }
        Console.WriteLine($"PASS: sizeof(INPUT)={structSize} bytes");

        var engine = new TelexEngine();

        string TypeWord(string keys, bool allowStandaloneW)
        {
            engine.Reset();
            string screen = "";
            foreach (char k in keys)
            {
                if (k == ' ')
                {
                    engine.HandleSpace();
                    screen += ' ';
                }
                else if (k == '\b')
                {
                    engine.HandleBackspace();
                    if (screen.Length > 0)
                        screen = screen.Substring(0, screen.Length - 1);
                }
                else if (engine.ProcessKey(k, allowStandaloneW, out int bs, out string rep))
                {
                    screen = screen.Substring(0, screen.Length - bs) + rep;
                }
                else
                {
                    screen += k;
                }
            }
            return screen;
        }

        var tests = new (string input, bool standaloneW, string expected)[]
        {
            // Kiểm tra lỗi bấm nhầm Space rồi Backspace quay lại gõ dấu tiếp ("Tun" + Space + Backspace + "fg" -> "Tùng")
            ("Tun \bfg", true, "Tùng"),
            ("Tung \bf", true, "Tùng"),
            ("Tun g\b\bfg", true, "Tùng"),
            ("Tun \bfg\b\bng", true, "Tùng"),

            // Kiểm tra lỗi gõ nhanh chữ "được" ("dudwojc", "dudowjc", "duwodjc", "duwocjd")
            ("dudwojc", true, "được"),
            ("dudowjc", true, "được"),
            ("duwodjc", true, "được"),
            ("duwocjd", true, "được"),

            // Kiểm tra lỗi "sửa" (gõ theo mọi thứ tự phím đều phải ra "sửa", không bao giờ ra "suẳ")
            ("suawr", true, "sửa"),
            ("suwar", true, "sửa"),
            ("suarw", true, "sửa"),
            ("swar", true, "sửa"),
            ("cuawr", true, "cửa"),
            ("muaw", true, "mưa"),
            ("vuawf", true, "vừa"),
            ("quawn", true, "quăn"),

            // Kiểm tra nhận diện từ Tiếng Anh (không tự ý thêm dấu Tiếng Việt)
            ("MiniTelex", true, "MiniTelex"),
            ("Telex", true, "Telex"),
            ("telex", true, "telex"),
            ("David", true, "David"),
            ("david", true, "david"),
            ("Valorant", true, "Valorant"),
            ("Discord", true, "Discord"),
            ("Server", true, "Server"),
            ("Service", true, "Service"),
            ("Text", true, "Text"),
            ("Soft", true, "Soft"),
            ("User", true, "User"),
            ("More", true, "More"),
            ("Google", true, "Google"),
            ("Windows", true, "Windows"),

            // Kiểm tra gõ Tiếng Việt chuẩn
            ("ddanhs", false, "đánh"),
            ("roofng", false, "rồng"),
            ("nguowif", false, "người"),
            ("nhw", false, "như"),
            ("thw", false, "thư"),
            ("thuowng", false, "thương"),
            ("tieengs", false, "tiếng"),
            ("vieetj", false, "việt"),
            ("hoaf", false, "hòa"),
            ("toans", false, "toán"),
            ("quas", false, "quá"),
            ("giwxa", false, "giữa"),
            ("w", false, "w"),
            ("w", true, "ư"),
            ("wf", false, "ừ"),
            ("ws", false, "ứ"),
            ("wng", false, "ưng"),
            ("uw", false, "ư"),
            ("DDax", true, "Đã"),
            ("mowr", true, "mở"),
            ("uwngs", true, "ứng"),
            ("dungj", true, "dụng"),
            ("dduwowcj", true, "được"),
            ("dduwocj", true, "được"),
            ("thuwowngf", true, "thường"),
            ("thuwongf", true, "thường")
        };

        foreach (var (input, standaloneW, expected) in tests)
        {
            string actual = TypeWord(input, standaloneW);
            if (actual != expected)
            {
                Console.Error.WriteLine($"FAIL: input='{input}' (standaloneW={standaloneW}) -> expected='{expected}', actual='{actual}'");
                return 1;
            }
        }

        Console.WriteLine("ALL TELEX & ENGLISH DETECTION TESTS PASSED!");
        return 0;
    }
}
