using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MusicLoom.App;

internal static class ExplorerIntegration
{
    public static bool TryRevealFile(
        string path,
        out Exception? error)
    {
        error = null;

        try
        {
            string fullPath =
                System.IO.Path.GetFullPath(
                    path);
            string? directory =
                System.IO.Path.GetDirectoryName(
                    fullPath);

            if (!File.Exists(fullPath)
                || string.IsNullOrWhiteSpace(
                    directory)
                || !Directory.Exists(directory))
            {
                return false;
            }

            IntPtr folderPidl =
                ILCreateFromPathW(
                    directory);
            IntPtr filePidl =
                ILCreateFromPathW(
                    fullPath);

            if (folderPidl
                    == IntPtr.Zero
                || filePidl
                    == IntPtr.Zero)
            {
                if (folderPidl
                    != IntPtr.Zero)
                {
                    ILFree(
                        folderPidl);
                }

                if (filePidl
                    != IntPtr.Zero)
                {
                    ILFree(
                        filePidl);
                }

                throw new Win32Exception(
                    "Windows Shell could not create an item identifier for the path.");
            }

            try
            {
                IntPtr childPidl =
                    ILFindLastID(
                        filePidl);

                if (childPidl
                    == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        "Windows Shell could not resolve the file item.");
                }

                int result =
                    SHOpenFolderAndSelectItems(
                        folderPidl,
                        1,
                        [childPidl],
                        0);

                if (result < 0)
                {
                    Marshal.ThrowExceptionForHR(
                        result);
                }

                return true;
            }
            finally
            {
                ILFree(
                    filePidl);
                ILFree(
                    folderPidl);
            }
        }
        catch (Exception exception)
        {
            error = exception;
            return false;
        }
    }

    [DllImport(
        "shell32.dll",
        EntryPoint = "ILCreateFromPathW",
        CharSet = CharSet.Unicode)]
    private static extern IntPtr
        ILCreateFromPathW(
            string path);

    [DllImport(
        "shell32.dll")]
    private static extern IntPtr
        ILFindLastID(
            IntPtr pidl);

    [DllImport(
        "shell32.dll")]
    private static extern int
        SHOpenFolderAndSelectItems(
            IntPtr pidlFolder,
            uint cidl,
            [MarshalAs(
                UnmanagedType.LPArray,
                SizeParamIndex = 1)]
            IntPtr[] apidl,
            uint flags);

    [DllImport(
        "shell32.dll")]
    private static extern void
        ILFree(
            IntPtr pidl);
}
