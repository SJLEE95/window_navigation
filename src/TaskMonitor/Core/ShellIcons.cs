using System.Runtime.InteropServices;

namespace TaskMonitor.Core;

/// <summary>32bpp BGRA (premultiplied alpha) 픽셀. WPF 에서는 PixelFormats.Pbgra32 로 그대로 사용.</summary>
public sealed record IconPixels(int Width, int Height, byte[] Bgra, string Method);

/// <summary>탐색기와 같은 아이콘 추출. 1순위 IShellItemImageFactory, 실패 시 SHGetFileInfo.</summary>
public static class ShellIcons
{
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint n);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int x, y; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    /// <param name="path">파일·폴더·.lnk 경로, 또는 "C:\x.dll,3" 같은 아이콘 위치</param>
    public static IconPixels? Get(string path, int size)
    {
        if (string.IsNullOrEmpty(path)) return null;
        // "파일,인덱스" 형식 또는 .ico 지정
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^(?<f>.+?),\s*(?<i>-?\d+)$");
        if (m.Success && File.Exists(m.Groups["f"].Value))
            return FromIconResource(m.Groups["f"].Value, int.Parse(m.Groups["i"].Value), size);
        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            return FromIconResource(path, 0, size);

        var r = FromShellItem(path, size);
        if (r is not null) return r;
        return FromFileInfo(path);
    }

    private static IconPixels? FromShellItem(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbm, "IShellItemImageFactory", premultiplied: true); }
            finally { DeleteObject(hbm); }
        }
        finally { Marshal.FinalReleaseComObject(factory); }
    }

    private static IconPixels? FromFileInfo(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (info.hIcon == IntPtr.Zero) return null;
        try { return FromHIcon(info.hIcon, "SHGetFileInfo"); }
        finally { DestroyIcon(info.hIcon); }
    }

    private static IconPixels? FromIconResource(string file, int index, int size)
    {
        var large = new IntPtr[1];
        if (ExtractIconEx(file, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return FromShellItem(file, size);
        try { return FromHIcon(large[0], "ExtractIconEx"); }
        finally { DestroyIcon(large[0]); }
    }

    private static IconPixels? FromHIcon(IntPtr hIcon, string method)
    {
        if (!GetIconInfo(hIcon, out var ii)) return null;
        try { return ii.hbmColor == IntPtr.Zero ? null : FromHBitmap(ii.hbmColor, method, premultiplied: false); }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    private static IconPixels? FromHBitmap(IntPtr hbm, string method, bool premultiplied)
    {
        var bm = new BITMAP();
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, // 위에서 아래로
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var bits = new byte[w * h * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbm, 0, (uint)h, bits, ref bi, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        bool anyAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        for (int i = 0; i < bits.Length; i += 4)
        {
            if (!anyAlpha) { bits[i + 3] = 255; continue; }   // 알파 없는 옛 아이콘: 불투명 처리
            if (!premultiplied)
            {
                int a = bits[i + 3];
                bits[i] = (byte)(bits[i] * a / 255);
                bits[i + 1] = (byte)(bits[i + 1] * a / 255);
                bits[i + 2] = (byte)(bits[i + 2] * a / 255);
            }
        }
        return new IconPixels(w, h, bits, method);
    }

    /// <summary>검증용: 32bpp BMP 로 저장 (알파 포함)</summary>
    public static void SaveBmp(IconPixels p, string path)
    {
        int rowBytes = p.Width * 4, dataSize = rowBytes * p.Height;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0x4D42); bw.Write(54 + dataSize); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(p.Width); bw.Write(-p.Height); bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0); bw.Write(dataSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        bw.Write(p.Bgra);
    }
}
