using System;
using System.Collections.Generic;
using System.Text;

namespace MiniTelex;

/// <summary>
/// Bộ xử lý gõ Tiếng Việt kiểu Telex thông minh (MiniTelex Engine).
/// - Chuẩn vị trí dấu Tiếng Việt ("sửa", "hòa", "toán", "thương", "được", "giữa"...)
/// - Tự động nhận diện từ Tiếng Anh ("MiniTelex", "David", "Valorant", "Discord", "Server", "Text"...)
///   để không bỏ dấu nhầm và tự hoàn tác về từ gốc Tiếng Anh nếu đang gõ dở.
/// </summary>
public sealed class TelexEngine
{
    // Nguyên âm cơ bản (index 0..11) -> 6 trạng thái dấu thanh (0: ngang, 1: sắc, 2: huyền, 3: hỏi, 4: ngã, 5: nặng)
    private static readonly char[,] VowelTable = new char[12, 6]
    {
        // a (0)
        { 'a', 'á', 'à', 'ả', 'ã', 'ạ' },
        // ă (1)
        { 'ă', 'ắ', 'ằ', 'ẳ', 'ẵ', 'ặ' },
        // â (2)
        { 'â', 'ấ', 'ầ', 'ẩ', 'ẫ', 'ậ' },
        // e (3)
        { 'e', 'é', 'è', 'ẻ', 'ẽ', 'ẹ' },
        // ê (4)
        { 'ê', 'ế', 'ề', 'ể', 'ễ', 'ệ' },
        // i (5)
        { 'i', 'í', 'ì', 'ỉ', 'ĩ', 'ị' },
        // o (6)
        { 'o', 'ó', 'ò', 'ỏ', 'õ', 'ọ' },
        // ô (7)
        { 'ô', 'ố', 'ồ', 'ổ', 'ỗ', 'ộ' },
        // ơ (8)
        { 'ơ', 'ớ', 'ờ', 'ở', 'ỡ', 'ợ' },
        // u (9)
        { 'u', 'ú', 'ù', 'ủ', 'ũ', 'ụ' },
        // ư (10)
        { 'ư', 'ứ', 'ừ', 'ử', 'ữ', 'ự' },
        // y (11)
        { 'y', 'ý', 'ỳ', 'ỷ', 'ỹ', 'ỵ' }
    };

    private static readonly Dictionary<char, (int vowelIdx, int toneIdx, bool isUpper)> CharLookup = BuildLookup();

    // Phụ âm đầu hợp lệ trong Tiếng Việt
    private static readonly HashSet<string> ValidInitialConsonants = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "b", "c", "ch", "d", "đ", "g", "gh", "gi", "h", "k", "kh",
        "l", "m", "n", "ng", "ngh", "nh", "p", "ph", "q", "qu", "r", "s", "t", "th", "tr", "v", "x"
    };

    // Phụ âm cuối hợp lệ trong Tiếng Việt (bao gồm cả c, ch, m, n, ng, nh, p, t, và khi đang gõ dở 'n' hoặc 'c')
    private static readonly HashSet<string> ValidFinalConsonants = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "c", "ch", "m", "n", "ng", "nh", "p", "t"
    };

    // Các cụm nguyên âm hợp lệ trong Tiếng Việt (dạng không dấu thanh, bao gồm cả trạng thái đang gõ dở trước khi bỏ dấu mũ/móc)
    private static readonly HashSet<string> ValidVowelClusters = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "ă", "â", "e", "ê", "i", "o", "ô", "ơ", "u", "ư", "y",
        "ai", "ao", "au", "ay", "âu", "ây",
        "eo", "êu", "eu",
        "ia", "iê", "iu", "ie",
        "oa", "oă", "oe", "oi", "ôi", "ơi", "oo",
        "ua", "uă", "uâ", "ưa", "uê", "ue", "ui", "ưi", "uo", "uô", "uơ", "ưo", "ươ", "ưu", "uu", "uy",
        "uya", "uyê", "uyu", "uye",
        "iêu", "ieu", "oai", "oao", "oay", "oeo", "uây", "uay",
        "ươi", "uoi", "ưoi", "uơi",
        "ươu", "uou", "ưou", "uơu",
        "yêu", "yeu", "ye"
    };

    private static Dictionary<char, (int vowelIdx, int toneIdx, bool isUpper)> BuildLookup()
    {
        var dict = new Dictionary<char, (int, int, bool)>();
        for (int v = 0; v < 12; v++)
        {
            for (int t = 0; t < 6; t++)
            {
                char lower = VowelTable[v, t];
                char upper = char.ToUpperInvariant(lower);
                dict[lower] = (v, t, false);
                dict[upper] = (v, t, true);
            }
        }
        return dict;
    }

    private sealed class CharCell
    {
        public char CurrentChar;
        public bool IsUpper;
        public int VowelIndex = -1; // -1 nếu là phụ âm
        public int ToneIndex = 0;   // 0..5

        public CharCell(char c)
        {
            CurrentChar = c;
            IsUpper = char.IsUpper(c);
            if (CharLookup.TryGetValue(c, out var info))
            {
                VowelIndex = info.vowelIdx;
                ToneIndex = info.toneIdx;
                IsUpper = info.isUpper;
            }
        }

        public CharCell Clone() => new(CurrentChar)
        {
            IsUpper = IsUpper,
            VowelIndex = VowelIndex,
            ToneIndex = ToneIndex
        };

        public void SyncChar()
        {
            if (VowelIndex >= 0)
            {
                char c = VowelTable[VowelIndex, ToneIndex];
                CurrentChar = IsUpper ? char.ToUpperInvariant(c) : c;
            }
        }
    }

    private sealed class WordSnapshot
    {
        public required List<CharCell> Cells;
        public required string RawWord;
        public bool HasTransformed;
        public int TrailingSpaces;
    }

    private readonly List<CharCell> _buffer = new();
    private readonly StringBuilder _rawWord = new();
    private readonly Stack<WordSnapshot> _wordHistory = new();
    private bool _disabledForCurrentWord;
    private bool _hasTransformedInCurrentWord;

    public int BufferLength => _buffer.Count;

    public string GetCurrentWord()
    {
        var sb = new StringBuilder(_buffer.Count);
        foreach (var cell in _buffer)
            sb.Append(cell.CurrentChar);
        return sb.ToString();
    }

    public bool IsPureWasdWord()
    {
        for (int i = 0; i < _rawWord.Length; i++)
        {
            char c = char.ToLowerInvariant(_rawWord[i]);
            if (c is not ('w' or 'a' or 's' or 'd'))
                return false;
        }
        return true;
    }

    public void Reset()
    {
        _buffer.Clear();
        _rawWord.Clear();
        _wordHistory.Clear();
        _disabledForCurrentWord = false;
        _hasTransformedInCurrentWord = false;
    }

    /// <summary>
    /// Ghi nhận khi bấm phím Space: Lưu lại từ vừa gõ vào lịch sử để nếu người dùng lỡ tay bấm Space
    /// rồi bấm Backspace quay lại (vd: "Tun" + Space + Backspace + "fg" -> "Tùng") thì vẫn ghép dấu tiếp được!
    /// </summary>
    public void HandleSpace()
    {
        if (_buffer.Count > 0)
        {
            var cloned = new List<CharCell>(_buffer.Count);
            foreach (var c in _buffer)
                cloned.Add(c.Clone());

            _wordHistory.Push(new WordSnapshot
            {
                Cells = cloned,
                RawWord = _rawWord.ToString(),
                HasTransformed = _hasTransformedInCurrentWord,
                TrailingSpaces = 1
            });

            _buffer.Clear();
            _rawWord.Clear();
            _disabledForCurrentWord = false;
            _hasTransformedInCurrentWord = false;
        }
        else if (_wordHistory.Count > 0)
        {
            _wordHistory.Peek().TrailingSpaces++;
        }
    }

    public void HandleBackspace()
    {
        if (_buffer.Count > 0)
        {
            _buffer.RemoveAt(_buffer.Count - 1);

            if (_buffer.Count == 0)
            {
                _rawWord.Clear();
                _disabledForCurrentWord = false;
                _hasTransformedInCurrentWord = false;
            }
            else
            {
                // Đồng bộ lại _rawWord và trạng thái dấu sau khi nhấn Backspace
                _rawWord.Clear();
                _rawWord.Append(GetCurrentWord());
                _hasTransformedInCurrentWord = HasAnyVietnameseMarkInBuffer();
                _disabledForCurrentWord = !IsValidVietnameseSyllableProgress();
            }
            return;
        }

        // Nếu buffer hiện tại đang rỗng mà người dùng bấm Backspace để xoá dấu Space vừa lỡ tay bấm:
        if (_wordHistory.Count > 0)
        {
            var top = _wordHistory.Peek();
            top.TrailingSpaces--;
            if (top.TrailingSpaces <= 0)
            {
                _wordHistory.Pop();
                _buffer.Clear();
                foreach (var c in top.Cells)
                    _buffer.Add(c.Clone());

                _rawWord.Clear();
                _rawWord.Append(top.RawWord);
                _hasTransformedInCurrentWord = top.HasTransformed;
                _disabledForCurrentWord = !IsValidVietnameseSyllableProgress();
            }
        }
    }

    private bool HasAnyVietnameseMarkInBuffer()
    {
        foreach (var c in _buffer)
        {
            if (char.ToLowerInvariant(c.CurrentChar) == 'đ')
                return true;
            if (c.VowelIndex >= 0 && (c.ToneIndex > 0 || c.VowelIndex is 1 or 2 or 4 or 7 or 8 or 10))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Xử lý 1 ký tự vừa gõ.
    /// Trả về true nếu phím này được biến đổi thành tiếng Việt (hoặc hoàn tác về từ Tiếng Anh).
    /// </summary>
    public bool ProcessKey(char inputChar, bool allowStandaloneW, out int backspacesNeeded, out string replacementText)
    {
        backspacesNeeded = 0;
        replacementText = string.Empty;

        if (!char.IsLetter(inputChar))
        {
            Reset();
            return false;
        }

        string oldWord = GetCurrentWord();
        _rawWord.Append(inputChar);

        // Nếu từ hiện tại đã được xác định là từ Tiếng Anh / huỷ Telex -> chỉ ghi nhận ký tự thô
        if (_disabledForCurrentWord)
        {
            _buffer.Add(new CharCell(inputChar));
            return false;
        }

        char lower = char.ToLowerInvariant(inputChar);
        bool isUpper = char.IsUpper(inputChar);

        // 1. Kiểm tra gõ 'dd' -> 'đ' (CHỈ khi 2 chữ d đi liền nhau ở đầu từ, tránh biến "David" -> "Đavid")
        if (lower == 'd')
        {
            if (TryHandleD(inputChar, oldWord, out backspacesNeeded, out replacementText))
            {
                _hasTransformedInCurrentWord = true;
                return true;
            }

            _buffer.Add(new CharCell(inputChar));
            return CheckEnglishAndRevertIfNeeded(oldWord, out backspacesNeeded, out replacementText);
        }

        // 2. Kiểm tra gõ trùng nguyên âm mũ: aa -> â, ee -> ê, oo -> ô
        if (lower is 'a' or 'e' or 'o')
        {
            if (TryHandleDoubleVowel(inputChar, lower, oldWord, out backspacesNeeded, out replacementText))
            {
                _hasTransformedInCurrentWord = true;
                return true;
            }

            _buffer.Add(new CharCell(inputChar));
            if (CheckEnglishAndRevertIfNeeded(oldWord, out backspacesNeeded, out replacementText))
                return true;

            if (RebalanceToneIfNeeded(oldWord, out backspacesNeeded, out replacementText))
            {
                _hasTransformedInCurrentWord = true;
                return true;
            }

            return false;
        }

        // 3. Kiểm tra phím 'w' (ă, ơ, ư, "sửa", "thương"...)
        if (lower == 'w')
        {
            if (TryHandleW(inputChar, isUpper, allowStandaloneW, oldWord, out backspacesNeeded, out replacementText))
            {
                _hasTransformedInCurrentWord = true;
                return true;
            }

            _buffer.Add(new CharCell(inputChar));
            return CheckEnglishAndRevertIfNeeded(oldWord, out backspacesNeeded, out replacementText);
        }

        // 4. Kiểm tra các phím dấu thanh: s (1), f (2), r (3), x (4), j (5), z (0)
        int toneTarget = lower switch
        {
            's' => 1,
            'f' => 2,
            'r' => 3,
            'x' => 4,
            'j' => 5,
            'z' => 0,
            _ => -1
        };

        if (toneTarget >= 0)
        {
            // Hỗ trợ wf -> ừ, ws -> ứ, wr -> ử, wx -> ữ, wj -> ự khi allowStandaloneW = false
            if (_buffer.Count == 1 && char.ToLowerInvariant(_buffer[0].CurrentChar) == 'w' && toneTarget > 0)
            {
                _buffer[0].VowelIndex = 10; // ư
                _buffer[0].ToneIndex = toneTarget;
                _buffer[0].SyncChar();
                _hasTransformedInCurrentWord = true;
                ComputeDiff(oldWord, GetCurrentWord(), out backspacesNeeded, out replacementText);
                return true;
            }

            if (TryHandleTone(inputChar, toneTarget, oldWord, out backspacesNeeded, out replacementText))
            {
                _hasTransformedInCurrentWord = true;
                return true;
            }

            _buffer.Add(new CharCell(inputChar));
            return CheckEnglishAndRevertIfNeeded(oldWord, out backspacesNeeded, out replacementText);
        }

        // 5. Hỗ trợ w + n/c/t/u/i/m/p/o ở đầu từ (vd: wng -> ưng, wc -> ức, wo -> ươ)
        if (_buffer.Count == 1 && char.ToLowerInvariant(_buffer[0].CurrentChar) == 'w' &&
            lower is 'n' or 'c' or 't' or 'u' or 'i' or 'm' or 'p' or 'o')
        {
            _buffer[0].VowelIndex = 10; // ư
            if (lower == 'o')
            {
                _buffer[0].SyncChar();
                char oHorn = isUpper ? 'Ơ' : 'ơ';
                _buffer.Add(new CharCell(oHorn));
            }
            else
            {
                _buffer[0].SyncChar();
                _buffer.Add(new CharCell(inputChar));
            }
            _hasTransformedInCurrentWord = true;
            ComputeDiff(oldWord, GetCurrentWord(), out backspacesNeeded, out replacementText);
            return true;
        }

        // 6. Các chữ cái còn lại
        _buffer.Add(new CharCell(inputChar));

        // Kiểm tra xem ký tự mới thêm vào có làm từ này trở thành từ Tiếng Anh (không hợp lệ trong Tiếng Việt) không
        if (CheckEnglishAndRevertIfNeeded(oldWord, out backspacesNeeded, out replacementText))
            return true;

        if (RebalanceToneIfNeeded(oldWord, out backspacesNeeded, out replacementText))
        {
            _hasTransformedInCurrentWord = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Kiểm tra cấu trúc âm tiết Tiếng Việt. Nếu từ đang gõ vi phạm cấu trúc Tiếng Việt (ví dụ đang gõ Tiếng Anh:
    /// "MiniTelex", "David", "Discord", "Server", "Service", "Text", "Soft", "Google", "Windows"...),
    /// tự động khoá Telex cho từ này và hoàn trả lại đúng chữ Tiếng Anh gốc trên màn hình!
    /// </summary>
    private bool CheckEnglishAndRevertIfNeeded(string oldScreenWord, out int backspaces, out string replacement)
    {
        backspaces = 0;
        replacement = string.Empty;

        if (IsValidVietnameseSyllableProgress())
            return false;

        // Từ hiện tại KHÔNG phải âm tiết Tiếng Việt hợp lệ -> Đánh dấu là từ Tiếng Anh!
        _disabledForCurrentWord = true;

        // Nếu trước đó lỡ biến đổi dấu trong từ này (vd gõ "ser" -> "sẻ", rồi gõ tiếp 'v' thành "service"),
        // hoàn tác lại toàn bộ chữ thô Tiếng Anh (_rawWord)!
        if (_hasTransformedInCurrentWord)
        {
            string rawStr = _rawWord.ToString();
            _buffer.Clear();
            foreach (char c in rawStr)
                _buffer.Add(new CharCell(c));

            ComputeDiff(oldScreenWord, rawStr, out backspaces, out replacement);
            return true;
        }

        return false;
    }

    private bool IsValidVietnameseSyllableProgress()
    {
        if (_buffer.Count == 0)
            return true;

        // Tách âm tiết thành 3 phần: [Phụ âm đầu] + [Cụm nguyên âm] + [Phụ âm cuối]
        var initial = new StringBuilder();
        var vowels = new StringBuilder();
        var finalCons = new StringBuilder();

        int stage = 0; // 0: initial, 1: vowels, 2: final consonant
        int activeTone = 0;

        for (int i = 0; i < _buffer.Count; i++)
        {
            var cell = _buffer[i];
            char lower = char.ToLowerInvariant(cell.CurrentChar);
            bool isVowel = cell.VowelIndex >= 0;

            // Trường hợp đặc biệt: "qu" và "gi" khi đứng trước nguyên âm khác thì 'u'/'i' thuộc về phụ âm đầu
            if (stage == 0 && isVowel)
            {
                if (cell.VowelIndex == 9 && initial.Length == 1 && initial[0] == 'q')
                {
                    initial.Append('u');
                    continue;
                }
                if (cell.VowelIndex == 5 && initial.Length == 1 && initial[0] == 'g' &&
                    i + 1 < _buffer.Count && _buffer[i + 1].VowelIndex >= 0)
                {
                    initial.Append('i');
                    continue;
                }
            }

            if (stage == 0)
            {
                if (!isVowel)
                {
                    // Cho phép chữ 'w' đơn ở đầu từ khi đang chờ gõ tiếp wf/ws/wng
                    if (_buffer.Count == 1 && lower == 'w')
                        return true;

                    initial.Append(lower);
                }
                else
                {
                    stage = 1;
                    vowels.Append(VowelTable[cell.VowelIndex, 0]);
                    if (cell.ToneIndex > 0) activeTone = cell.ToneIndex;
                }
            }
            else if (stage == 1)
            {
                if (isVowel)
                {
                    vowels.Append(VowelTable[cell.VowelIndex, 0]);
                    if (cell.ToneIndex > 0) activeTone = cell.ToneIndex;
                }
                else
                {
                    stage = 2;
                    finalCons.Append(lower);
                }
            }
            else // stage == 2 (đã sang phụ âm cuối)
            {
                if (isVowel)
                {
                    // Có nguyên âm xuất hiện sau phụ âm cuối (V-C-V như "Mini", "Tele", "Game", "Valo", "David")
                    // -> Chắc chắn 100% là từ Tiếng Anh / đa âm tiết!
                    return false;
                }
                finalCons.Append(lower);
            }
        }

        // 1. Kiểm tra phụ âm đầu
        if (!ValidInitialConsonants.Contains(initial.ToString()))
            return false;

        // 2. Kiểm tra cụm nguyên âm (nếu đã có nguyên âm)
        if (vowels.Length > 0)
        {
            string vStr = vowels.ToString();
            if (!ValidVowelClusters.Contains(vStr))
                return false;

            // Nguyên âm "uă" chỉ đi sau "q" (quă), không bao giờ đi sau phụ âm khác
            if (vStr == "uă" && !initial.ToString().Equals("q", StringComparison.OrdinalIgnoreCase))
                return false;

            // Chặn các từ Tiếng Anh có cấu trúc [Nguyên âm] + [s/r/f/x/j] + [e] như:
            // "use", "user", "sure", "pure", "more", "core", "rose", "nose", "lose", "hire", "tire", "rise", "wise"
            // (Vì trong Tiếng Việt không có âm tiết nào gõ dấu thanh xong mới gõ chữ 'e' đơn tạo thành "ue", "oe", "ie")
            if (activeTone > 0)
            {
                if (vStr.Equals("oe", StringComparison.OrdinalIgnoreCase) ||
                    vStr.Equals("ie", StringComparison.OrdinalIgnoreCase) ||
                    (vStr.Equals("ue", StringComparison.OrdinalIgnoreCase) && !initial.ToString().Equals("q", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
        }

        // 3. Kiểm tra phụ âm cuối
        if (finalCons.Length > 0)
        {
            string fStr = finalCons.ToString();
            if (!ValidFinalConsonants.Contains(fStr))
                return false;

            // Luật thanh điệu Tiếng Việt: Âm tiết kết thúc bằng c, ch, p, t CHỈ đi với thanh Ngang (0), Sắc (1) hoặc Nặng (5).
            // Không bao giờ đi với Huyền (2 - f), Hỏi (3 - r), Ngã (4 - x) -> Chặn "text", "next", "soft", "left"!
            if (fStr is "c" or "ch" or "p" or "t")
            {
                if (activeTone is 2 or 3 or 4)
                    return false;
            }
        }

        return true;
    }

    private bool TryHandleD(char rawInput, string oldWord, out int backspaces, out string replacement)
    {
        backspaces = 0;
        replacement = string.Empty;

        if (_buffer.Count == 0 || !IsValidVietnameseSyllableProgress())
            return false;

        char firstLower = char.ToLowerInvariant(_buffer[0].CurrentChar);

        // Hỗ trợ cả gõ "dd" liền nhau lẫn gõ nhanh 2 tay kiểu "dudwojc" / "dudowjc" -> "được",
        // nhưng chỉ khi âm tiết hiện tại là âm tiết Tiếng Việt hợp lệ (nên "David", "Discord", "Dead" đã bị chặn từ trước đó!)
        if (firstLower == 'd')
        {
            _buffer[0].CurrentChar = _buffer[0].IsUpper ? 'Đ' : 'đ';
            ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
            return true;
        }

        if (_buffer.Count == 1 && firstLower == 'đ')
        {
            // Hoàn tác đ + d -> dd
            _buffer[0].CurrentChar = _buffer[0].IsUpper ? 'D' : 'd';
            _buffer.Add(new CharCell(rawInput));
            _disabledForCurrentWord = true;
            ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
            return true;
        }

        return false;
    }

    private bool TryHandleDoubleVowel(char rawInput, char lower, string oldWord, out int backspaces, out string replacement)
    {
        backspaces = 0;
        replacement = string.Empty;

        if (_buffer.Count == 0 || !IsValidVietnameseSyllableProgress())
            return false;

        int baseVowelIdx = lower switch
        {
            'a' => 0,
            'e' => 3,
            'o' => 6,
            _ => -1
        };
        int circumflexIdx = lower switch
        {
            'a' => 2, // â
            'e' => 4, // ê
            'o' => 7, // ô
            _ => -1
        };

        // Hoàn tác nếu gõ 3 lần liên tiếp (aaa -> aa, eee -> ee, ooo -> oo)
        if (_buffer[^1].VowelIndex == circumflexIdx)
        {
            _buffer[^1].VowelIndex = baseVowelIdx;
            _buffer[^1].SyncChar();
            _buffer.Add(new CharCell(rawInput));
            _disabledForCurrentWord = true;
            ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
            return true;
        }

        // Chỉ cho phép ghép mũ (aa -> â, ee -> ê, oo -> ô) khi nguyên âm gốc nằm ngay sát cuối
        // HOẶC cách bởi đúng 1 phụ âm cuối hợp lệ Tiếng Việt (như "toan" + a -> "tân" / "tan" + a -> "tân", "ten" + e -> "tên")
        // Tuyệt đối không nhảy qua phụ âm 'l', 'v', 'r', 's'... để tránh "Telex" -> "Têlex"!
        for (int i = _buffer.Count - 1; i >= 0; i--)
        {
            var cell = _buffer[i];
            if (cell.VowelIndex == baseVowelIdx)
            {
                // Kiểm tra thử nếu đổi thành â/ê/ô thì cụm nguyên âm có hợp lệ trong Tiếng Việt không (vd "oa" + a -> không hợp lệ, trừ "uâ", "iê", "yê", "uô")
                int savedVowel = cell.VowelIndex;
                cell.VowelIndex = circumflexIdx;
                cell.SyncChar();

                if (!IsValidVietnameseSyllableProgress())
                {
                    cell.VowelIndex = savedVowel;
                    cell.SyncChar();
                    return false;
                }

                RebalanceToneInternal();
                ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
                return true;
            }
        }

        return false;
    }

    private bool TryHandleW(char rawInput, bool isUpper, bool allowStandaloneW, string oldWord, out int backspaces, out string replacement)
    {
        backspaces = 0;
        replacement = string.Empty;

        if (_buffer.Count > 0)
        {
            if (!IsValidVietnameseSyllableProgress())
                return false;

            // 1. Kiểm tra cụm nguyên âm "ua" hoặc "uo" trong âm tiết:
            // - Với "ua" (như "sua" + w -> "sưa", "mua" + w -> "mưa", "cua" + w -> "cưa"):
            //   Nếu KHÔNG phải cụm "qua" (phụ âm 'q' đứng trước 'u'), thì 'w' PHẢI biến 'u' thành 'ư' ("ưa"), KHÔNG biến 'a' thành 'ă' ("suă")!
            for (int i = 0; i < _buffer.Count - 1; i++)
            {
                bool isQu = i > 0 && char.ToLowerInvariant(_buffer[i - 1].CurrentChar) == 'q';
                if (!isQu && _buffer[i].VowelIndex == 9 && _buffer[i + 1].VowelIndex == 0) // 'u' + 'a' -> 'ư' + 'a'
                {
                    _buffer[i].VowelIndex = 10; // u -> ư
                    _buffer[i].SyncChar();
                    RebalanceToneInternal();
                    ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
                    return true;
                }

                // Với "uo" / "ưo" / "uơ" -> "ươ" (vd "thuong" + w -> "thương", "duoc" + w -> "được")
                if (!isQu && (_buffer[i].VowelIndex is 9 or 10) && (_buffer[i + 1].VowelIndex is 6 or 7 or 8))
                {
                    if (_buffer[i].VowelIndex == 10 && _buffer[i + 1].VowelIndex == 8 && i + 1 == _buffer.Count - 1)
                    {
                        // Hoàn tác ươ + w -> uow
                        _buffer[i].VowelIndex = 9;
                        _buffer[i].SyncChar();
                        _buffer[i + 1].VowelIndex = 6;
                        _buffer[i + 1].SyncChar();
                        _buffer.Add(new CharCell(rawInput));
                        _disabledForCurrentWord = true;
                        ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
                        return true;
                    }

                    _buffer[i].VowelIndex = 10; // ư
                    _buffer[i].SyncChar();
                    _buffer[i + 1].VowelIndex = 8; // ơ
                    _buffer[i + 1].SyncChar();
                    RebalanceToneInternal();
                    ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
                    return true;
                }
            }

            // 2. Tìm nguyên âm a, o, u đơn lẻ trong âm tiết để biến thành ă, ơ, ư
            for (int i = _buffer.Count - 1; i >= 0; i--)
            {
                var cell = _buffer[i];
                if (cell.VowelIndex is 0 or 6 or 9) // a, o, u
                {
                    // Bỏ qua 'u' trong "qu" nếu sau đó có nguyên âm khác (vd "qua" + w -> "quă")
                    if (cell.VowelIndex == 9 && i > 0 && char.ToLowerInvariant(_buffer[i - 1].CurrentChar) == 'q' && i < _buffer.Count - 1)
                        continue;

                    int oldV = cell.VowelIndex;
                    cell.VowelIndex = cell.VowelIndex switch
                    {
                        0 => 1,  // a -> ă
                        6 => 8,  // o -> ơ
                        9 => 10, // u -> ư
                        _ => cell.VowelIndex
                    };
                    cell.SyncChar();

                    if (!IsValidVietnameseSyllableProgress())
                    {
                        cell.VowelIndex = oldV;
                        cell.SyncChar();
                        continue;
                    }

                    RebalanceToneInternal();
                    ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
                    return true;
                }

                // Hoàn tác nếu gõ w lần 2 ngay sau ă, ơ, ư
                if (i == _buffer.Count - 1 && cell.VowelIndex is 1 or 8 or 10)
                {
                    if (cell.VowelIndex == 10 && _buffer.Count == 1)
                    {
                        _buffer.Clear();
                        _buffer.Add(new CharCell(cell.IsUpper ? 'W' : 'w'));
                        _disabledForCurrentWord = true;
                        ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
                        return true;
                    }

                    cell.VowelIndex = cell.VowelIndex switch
                    {
                        1 => 0,
                        8 => 6,
                        10 => 9,
                        _ => cell.VowelIndex
                    };
                    cell.SyncChar();
                    _buffer.Add(new CharCell(rawInput));
                    _disabledForCurrentWord = true;
                    ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
                    return true;
                }
            }

            // 3. Nếu trong buffer mới chỉ có phụ âm đầu Tiếng Việt hợp lệ (vd nh, th, tr, ch, gi, c, t, m, l, s, v...) -> gõ 'w' thành 'ư'
            if (IsOnlyInitialConsonantInBuffer())
            {
                char uHorn = isUpper ? 'Ư' : 'ư';
                _buffer.Add(new CharCell(uHorn));
                RebalanceToneInternal();
                ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
                return true;
            }
        }
        else if (allowStandaloneW)
        {
            char uHorn = isUpper ? 'Ư' : 'ư';
            _buffer.Add(new CharCell(uHorn));
            backspaces = 0;
            replacement = uHorn.ToString();
            return true;
        }

        return false;
    }

    private bool TryHandleTone(char rawInput, int targetTone, string oldWord, out int backspaces, out string replacement)
    {
        backspaces = 0;
        replacement = string.Empty;

        if (!IsValidVietnameseSyllableProgress())
            return false;

        int vowelPos = FindToneTargetVowelIndex();
        if (vowelPos < 0)
            return false;

        int existingTone = GetCurrentWordTone(out int tonedIdx);

        if (targetTone == 0 && existingTone == 0)
            return false;

        // Hoàn tác nếu gõ lặp chính dấu thanh đang có (vd: as + s -> as)
        if (targetTone != 0 && existingTone == targetTone && tonedIdx >= 0)
        {
            _buffer[tonedIdx].ToneIndex = 0;
            _buffer[tonedIdx].SyncChar();
            _buffer.Add(new CharCell(rawInput));
            _disabledForCurrentWord = true;
            ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
            return true;
        }

        // Lưu trạng thái cũ để kiểm tra luật thanh điệu (vd từ kết thúc bằng c/ch/p/t không nhận dấu f/r/x)
        int prevToneAtTarget = _buffer[vowelPos].ToneIndex;
        if (tonedIdx >= 0)
        {
            _buffer[tonedIdx].ToneIndex = 0;
            _buffer[tonedIdx].SyncChar();
        }

        _buffer[vowelPos].ToneIndex = targetTone;
        _buffer[vowelPos].SyncChar();

        if (!IsValidVietnameseSyllableProgress())
        {
            _buffer[vowelPos].ToneIndex = prevToneAtTarget;
            _buffer[vowelPos].SyncChar();
            if (tonedIdx >= 0)
            {
                _buffer[tonedIdx].ToneIndex = existingTone;
                _buffer[tonedIdx].SyncChar();
            }
            return false;
        }

        ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
        return true;
    }

    private bool RebalanceToneIfNeeded(string oldWord, out int backspaces, out string replacement)
    {
        backspaces = 0;
        replacement = string.Empty;
        bool modified = false;

        // Tự động chuẩn hóa "ưo" + phụ âm cuối/bán nguyên âm (c, n, t, m, p, i, u) -> "ươ"
        if (_buffer.Count >= 3)
        {
            for (int i = 0; i < _buffer.Count - 2; i++)
            {
                if (_buffer[i].VowelIndex == 10 && _buffer[i + 1].VowelIndex == 6)
                {
                    char nextLower = char.ToLowerInvariant(_buffer[i + 2].CurrentChar);
                    if (nextLower is 'c' or 'n' or 't' or 'm' or 'p' or 'i' or 'u')
                    {
                        _buffer[i + 1].VowelIndex = 8; // o -> ơ
                        _buffer[i + 1].SyncChar();
                        modified = true;
                    }
                }
            }
        }

        int existingTone = GetCurrentWordTone(out int oldToneIdx);
        if (existingTone > 0 && oldToneIdx >= 0)
        {
            int newToneIdx = FindToneTargetVowelIndex();
            if (newToneIdx >= 0 && newToneIdx != oldToneIdx)
            {
                _buffer[oldToneIdx].ToneIndex = 0;
                _buffer[oldToneIdx].SyncChar();
                _buffer[newToneIdx].ToneIndex = existingTone;
                _buffer[newToneIdx].SyncChar();
                modified = true;
            }
        }

        if (modified)
        {
            ComputeDiff(oldWord, GetCurrentWord(), out backspaces, out replacement);
            return true;
        }

        return false;
    }

    private void RebalanceToneInternal()
    {
        int existingTone = GetCurrentWordTone(out int oldToneIdx);
        if (existingTone == 0 || oldToneIdx < 0)
            return;

        int newToneIdx = FindToneTargetVowelIndex();
        if (newToneIdx >= 0 && newToneIdx != oldToneIdx)
        {
            _buffer[oldToneIdx].ToneIndex = 0;
            _buffer[oldToneIdx].SyncChar();
            _buffer[newToneIdx].ToneIndex = existingTone;
            _buffer[newToneIdx].SyncChar();
        }
    }

    private int GetCurrentWordTone(out int vowelIdxWithTone)
    {
        vowelIdxWithTone = -1;
        for (int i = 0; i < _buffer.Count; i++)
        {
            if (_buffer[i].VowelIndex >= 0 && _buffer[i].ToneIndex > 0)
            {
                vowelIdxWithTone = i;
                return _buffer[i].ToneIndex;
            }
        }
        return 0;
    }

    private bool IsOnlyInitialConsonantInBuffer()
    {
        if (_buffer.Count == 0 || _buffer.Count > 3)
            return false;

        var sb = new StringBuilder();
        foreach (var c in _buffer)
            sb.Append(char.ToLowerInvariant(c.CurrentChar));

        string cons = sb.ToString();
        if (cons == "gi")
            return true;

        foreach (var c in _buffer)
        {
            if (c.VowelIndex >= 0)
                return false;
        }

        return ValidInitialConsonants.Contains(cons) && cons is not ("q" or "qu");
    }

    private int FindToneTargetVowelIndex()
    {
        var vowelPositions = new List<int>();
        for (int i = 0; i < _buffer.Count; i++)
        {
            if (_buffer[i].VowelIndex >= 0)
            {
                if (_buffer[i].VowelIndex == 9 && i > 0 && char.ToLowerInvariant(_buffer[i - 1].CurrentChar) == 'q' &&
                    i + 1 < _buffer.Count && _buffer[i + 1].VowelIndex >= 0)
                {
                    continue;
                }

                if (_buffer[i].VowelIndex == 5 && i > 0 && char.ToLowerInvariant(_buffer[i - 1].CurrentChar) == 'g' &&
                    i + 1 < _buffer.Count && _buffer[i + 1].VowelIndex >= 0)
                {
                    continue;
                }

                vowelPositions.Add(i);
            }
        }

        if (vowelPositions.Count == 0)
            return -1;

        if (vowelPositions.Count == 1)
            return vowelPositions[0];

        // Ưu tiên cụm "ươ" -> đặt dấu vào 'ơ'
        for (int k = 0; k < vowelPositions.Count - 1; k++)
        {
            int pos1 = vowelPositions[k];
            int pos2 = vowelPositions[k + 1];
            if (_buffer[pos1].VowelIndex == 10 && _buffer[pos2].VowelIndex == 8)
                return pos2;
        }

        // Ưu tiên nguyên âm có dấu mũ/móc (ă, â, ê, ô, ơ, ư):
        // Lưu ý: với cụm "ưa" (như "sửa", "cửa", "mưa", "vừa", "lừa"), dấu thanh luôn nằm trên chữ 'ư' (nguyên âm thứ 1)!
        foreach (int pos in vowelPositions)
        {
            int v = _buffer[pos].VowelIndex;
            if (v is 1 or 2 or 4 or 7 or 8 or 10)
                return pos;
        }

        bool hasTrailingConsonant = vowelPositions[^1] < _buffer.Count - 1;

        if (vowelPositions.Count == 2)
        {
            if (hasTrailingConsonant)
                return vowelPositions[1];
            return vowelPositions[0];
        }

        return vowelPositions[1];
    }

    private static void ComputeDiff(string oldWord, string newWord, out int backspaces, out string replacement)
    {
        int commonPrefix = 0;
        int minLen = Math.Min(oldWord.Length, newWord.Length);
        while (commonPrefix < minLen && oldWord[commonPrefix] == newWord[commonPrefix])
        {
            commonPrefix++;
        }

        backspaces = oldWord.Length - commonPrefix;
        replacement = newWord.Substring(commonPrefix);
    }
}
