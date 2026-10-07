using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MusicLoom.App;

internal static partial class ExplorerIntegration
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

    [LibraryImport(
        "shell32.dll",
        EntryPoint = "ILCreateFromPathW",
        StringMarshalling =
            StringMarshalling.Utf16)]
    private static partial IntPtr
        ILCreateFromPathW(
            string path);

    [LibraryImport(
        "shell32.dll")]
    private static partial IntPtr
        ILFindLastID(
            IntPtr pidl);

    [LibraryImport(
        "shell32.dll")]
    private static partial int
        SHOpenFolderAndSelectItems(
            IntPtr pidlFolder,
            uint cidl,
            [MarshalAs(
                UnmanagedType.LPArray,
                SizeParamIndex = 1)]
            IntPtr[] apidl,
            uint flags);

    [LibraryImport(
        "shell32.dll")]
    private static partial void
        ILFree(
            IntPtr pidl);
}
