using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TaskMonitor.Core;

public sealed record LinkInfo(string Target, string? Args, string? Workdir, string? IconPath, int IconIndex);

/// <summary>Windows 바로가기(.lnk)와 인터넷 바로가기(.url) 읽기 (IShellLinkW COM)</summary>
public static class ShellLinks
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private const uint SLGP_RAWPATH = 0x4;

    /// <summary>.lnk 의 대상·인자·시작 폴더·아이콘. 스토어 앱처럼 파일 경로가 없는 바로가기는 Target 이 빈 문자열.</summary>
    public static LinkInfo? Read(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new CShellLink();
            ((IPersistFile)obj).Load(lnkPath, 0 /* STGM_READ */);
            var link = (IShellLinkW)obj;
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            string target = Environment.ExpandEnvironmentVariables(sb.ToString());
            sb.Clear();
            link.GetArguments(sb, sb.Capacity);
            string args = sb.ToString();
            sb.Clear();
            link.GetWorkingDirectory(sb, sb.Capacity);
            string wd = Environment.ExpandEnvironmentVariables(sb.ToString());
            sb.Clear();
            link.GetIconLocation(sb, sb.Capacity, out int idx);
            string icon = Environment.ExpandEnvironmentVariables(sb.ToString());
            return new LinkInfo(target, args.Length > 0 ? args : null, wd.Length > 0 ? wd : null,
                                icon.Length > 0 ? icon : null, idx);
        }
        catch { return null; }
        finally
        {
            if (obj is not null) Marshal.FinalReleaseComObject(obj);
        }
    }

    /// <summary>.lnk 만들기 (검증·데모용)</summary>
    public static void Create(string lnkPath, string target, string? args = null, string? workdir = null)
    {
        object obj = new CShellLink();
        try
        {
            var link = (IShellLinkW)obj;
            link.SetPath(target);
            if (args is not null) link.SetArguments(args);
            if (workdir is not null) link.SetWorkingDirectory(workdir);
            ((IPersistFile)obj).Save(lnkPath, true);
        }
        finally { Marshal.FinalReleaseComObject(obj); }
    }

    /// <summary>.url 파일의 URL= 줄</summary>
    public static string? ReadUrlFile(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line[4..].Trim();
        }
        catch { }
        return null;
    }
}
