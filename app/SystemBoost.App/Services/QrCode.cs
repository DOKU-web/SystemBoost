// QR code (ISO/IEC 18004, model 2) for the 2단계 인증 set-up: the otpauth:// address an authenticator app scans.
// Byte mode only, versions 1 to 40, all four error correction levels. No package: this file is the whole encoder,
// written after Project Nayuki's public description of the algorithm, and checked in SystemBoost.Account.Tests
// against matrices from an independent implementation (python-qrcode) for every version and level.
// Pure: no Avalonia, no app classes (the test project links this file).
using System;
using System.Collections.Generic;
using System.Text;

namespace SystemBoost.App.Services;

public sealed class QrCode
{
    public enum Ecc { Low, Medium, Quartile, High }

    public int Version { get; }
    public int Size { get; }
    public Ecc Level { get; }
    public int Mask { get; }

    private readonly bool[,] _dark;       // [y, x]
    private readonly bool[,] _function;   // finder, timing, alignment, format and version modules: never masked

    /// True for a dark module. x is the column, y the row, both from 0 at the top left (no quiet zone).
    public bool Dark(int x, int y) => _dark[y, x];

    /// The text as UTF-8 in the smallest version that holds it.
    public static QrCode FromText(string text, Ecc ecc = Ecc.Medium) => FromBytes(Encoding.UTF8.GetBytes(text), ecc);

    /// mask -1 picks the mask with the lowest penalty, as the standard asks; 0..7 forces one (tests).
    public static QrCode FromBytes(byte[] data, Ecc ecc = Ecc.Medium, int minVersion = 1, int maxVersion = 40, int mask = -1)
    {
        if (minVersion < 1 || maxVersion > 40 || minVersion > maxVersion) throw new ArgumentOutOfRangeException(nameof(minVersion));
        if (mask < -1 || mask > 7) throw new ArgumentOutOfRangeException(nameof(mask));
        for (int v = minVersion; ; v++)
        {
            int countBits = v < 10 ? 8 : 16;
            if (data.Length < 1 << countBits && 4 + countBits + data.Length * 8L <= DataCodewords(v, ecc) * 8L)
                return new QrCode(v, ecc, Codewords(data, v, ecc, countBits), mask);
            if (v >= maxVersion) throw new ArgumentException("The data does not fit in a QR code of these versions.", nameof(data));
        }
    }

    /// The most bytes one version holds at one level (for tests and callers that want to check first).
    public static int ByteCapacity(int version, Ecc ecc) => (DataCodewords(version, ecc) * 8 - 4 - (version < 10 ? 8 : 16)) / 8;

    // ------------------------------------------------------------ data bits
    private static byte[] Codewords(byte[] data, int version, Ecc ecc, int countBits)
    {
        int capacity = DataCodewords(version, ecc) * 8;
        var bits = new List<bool>(capacity);
        void Put(int value, int length) { for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Put(0b0100, 4);   // byte mode
        Put(data.Length, countBits);
        foreach (var b in data) Put(b, 8);
        Put(0, Math.Min(4, capacity - bits.Count));   // terminator
        Put(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Put(pad, 8);
        var words = new byte[capacity / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) words[i >> 3] |= (byte)(0x80 >> (i & 7));
        return words;
    }

    private QrCode(int version, Ecc ecc, byte[] dataCodewords, int mask)
    {
        Version = version;
        Level = ecc;
        Size = version * 4 + 17;
        _dark = new bool[Size, Size];
        _function = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(dataCodewords));
        if (mask == -1)
        {
            int best = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                ApplyMask(m);
                DrawFormatBits(m);
                int penalty = Penalty();
                if (penalty < best) { best = penalty; mask = m; }
                ApplyMask(m);   // XOR again: undone
            }
        }
        Mask = mask;
        ApplyMask(mask);
        DrawFormatBits(mask);
    }

    // ------------------------------------------------------------ function patterns
    private void Set(int x, int y, bool dark)
    {
        _dark[y, x] = dark;
        _function[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);
        var align = AlignmentPositions();
        int n = align.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0)) DrawAlignment(align[i], align[j]);
        DrawFormatBits(0);   // reserves the area; the real bits come with the mask
        DrawVersion();
    }

    private void DrawFinder(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy)), xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) Set(xx, yy, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                Set(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return Array.Empty<int>();
        int count = Version / 7 + 2;
        int step = (Version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private static readonly int[] FormatLevel = { 1, 0, 3, 2 };   // L, M, Q, H as the format bits spell them

    /// The 15 format bits for a level and mask (public for the tests).
    public static int FormatBits(Ecc ecc, int mask)
    {
        int data = FormatLevel[(int)ecc] << 3 | mask, rem = data;
        for (int i = 0; i < 10; i++) rem = rem << 1 ^ (rem >> 9) * 0x537;
        return (data << 10 | rem) ^ 0x5412;
    }

    private static bool Bit(int value, int i) => ((value >> i) & 1) != 0;

    private void DrawFormatBits(int mask)
    {
        int bits = FormatBits(Level, mask);
        for (int i = 0; i <= 5; i++) Set(8, i, Bit(bits, i));
        Set(8, 7, Bit(bits, 6));
        Set(8, 8, Bit(bits, 7));
        Set(7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(bits, i));
        for (int i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(bits, i));
        Set(8, Size - 8, true);   // the dark module
    }

    /// The 18 version bits (versions 7 and up; public for the tests).
    public static int VersionBits(int version)
    {
        int rem = version;
        for (int i = 0; i < 12; i++) rem = rem << 1 ^ (rem >> 11) * 0x1F25;
        return version << 12 | rem;
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        int bits = VersionBits(Version);
        for (int i = 0; i < 18; i++)
        {
            bool bit = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            Set(a, b, bit);
            Set(b, a, bit);
        }
    }

    // ------------------------------------------------------------ error correction
    private static readonly int[,] EccPerBlock =
    {
        { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly int[,] Blocks =
    {
        { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    private static int RawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version, Ecc ecc) =>
        RawDataModules(version) / 8 - EccPerBlock[(int)ecc, version] * Blocks[(int)ecc, version];

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int blocks = Blocks[(int)Level, Version], eccLen = EccPerBlock[(int)Level, Version];
        int raw = RawDataModules(Version) / 8, shortBlocks = blocks - raw % blocks, shortLen = raw / blocks;
        var divisor = ReedSolomonDivisor(eccLen);
        var all = new List<byte[]>(blocks);
        for (int i = 0, k = 0; i < blocks; i++)
        {
            int len = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
            var dat = new byte[len];
            Array.Copy(data, k, dat, 0, len);
            k += len;
            var ecc = ReedSolomonRemainder(dat, divisor);
            var block = new byte[shortLen + 1];   // short blocks keep one unused byte before their ECC
            Array.Copy(dat, block, len);
            Array.Copy(ecc, 0, block, shortLen + 1 - eccLen, eccLen);
            all.Add(block);
        }
        var result = new byte[raw];
        int n = 0;
        for (int i = 0; i < shortLen + 1; i++)
            for (int j = 0; j < blocks; j++)
                if (i != shortLen - eccLen || j >= shortBlocks) result[n++] = all[j][i];
        return result;
    }

    /// The generator polynomial of a degree, highest coefficient first and the leading 1 left out (public for the tests).
    public static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    public static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = z << 1 ^ (z >> 7) * 0x11D;
            z ^= (y >> i & 1) * x;
        }
        return (byte)z;
    }

    // ------------------------------------------------------------ placing and masking
    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;   // the vertical timing pattern
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_function[y, x] && i < data.Length * 8)
                    {
                        _dark[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !_function[y, x]) _dark[y, x] = !_dark[y, x];
            }
    }

    // The four penalty rules of the standard; the quiet zone counts as light for the finder-like rule.
    private int Penalty()
    {
        int result = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < Size; a++)
            {
                bool color = false;
                int run = 0;
                var history = new int[7];
                for (int b = 0; b < Size; b++)
                {
                    bool here = pass == 0 ? _dark[a, b] : _dark[b, a];
                    if (here == color)
                    {
                        run++;
                        if (run == 5) result += 3;
                        else if (run > 5) result++;
                    }
                    else
                    {
                        AddHistory(run, history);
                        if (!color) result += FinderLike(history) * 40;
                        color = here;
                        run = 1;
                    }
                }
                if (color) { AddHistory(run, history); run = 0; }
                AddHistory(run + Size, history);
                result += FinderLike(history) * 40;
            }
        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = _dark[y, x];
                if (c == _dark[y, x + 1] && c == _dark[y + 1, x] && c == _dark[y + 1, x + 1]) result += 3;
            }
        int dark = 0;
        foreach (var m in _dark) if (m) dark++;
        int total = Size * Size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return result + k * 10;
    }

    private void AddHistory(int run, int[] history)
    {
        if (history[0] == 0) run += Size;   // the light quiet zone before the first run
        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = run;
    }

    private static int FinderLike(int[] h)
    {
        int n = h[1];
        bool core = n > 0 && h[2] == n && h[3] == n * 3 && h[4] == n && h[5] == n;
        return (core && h[0] >= n * 4 && h[6] >= n ? 1 : 0) + (core && h[6] >= n * 4 && h[0] >= n ? 1 : 0);
    }
}
