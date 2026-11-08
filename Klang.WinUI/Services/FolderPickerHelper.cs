using System;
using System.Runtime.InteropServices;

namespace Klang_WinUI.Services;

public static class FolderPickerHelper
{
    [DllImport("shell32.dll")]
    private static extern int SHILCreateFromPath([MarshalAs(UnmanagedType.LPWStr)] string pszPath, out IntPtr ppIdl, ref uint rgflnOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, [Out] char[] pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;
    private const uint BIF_USENEWUI = BIF_NEWDIALOGSTYLE | 0x0010;

    public static string? PickFolder(IntPtr hwndOwner, string title = "选择音乐文件夹")
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = hwndOwner,
            pidlRoot = IntPtr.Zero,
            pszDisplayName = IntPtr.Zero,
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI,
            lpfn = IntPtr.Zero,
            lParam = IntPtr.Zero,
            iImage = 0
        };

        IntPtr pidl = SHBrowseForFolder(ref bi);
        if (pidl != IntPtr.Zero)
        {
            char[] path = new char[260];
            if (SHGetPathFromIDList(pidl, path))
            {
                CoTaskMemFree(pidl);
                return new string(path).TrimEnd('\0');
            }
            CoTaskMemFree(pidl);
        }

        return null;
    }
}
