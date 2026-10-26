using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
///     Creates directory links for filesystem tests. Windows junctions avoid the symlink privilege
///     requirement and exercise the same reparse-point behavior these tests cover.
/// </summary>
internal static class DirectoryLinkTestHelper
{
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static void Create(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        Directory.CreateDirectory(linkPath);

        try
        {
            using var handle = CreateFileW(
                linkPath,
                GenericWrite,
                ShareAll,
                nint.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                nint.Zero);

            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the junction directory.");
            }

            var reparseData = CreateMountPointReparseData(targetPath);
            if (!DeviceIoControl(
                    handle,
                    FsctlSetReparsePoint,
                    reparseData,
                    (uint)reparseData.Length,
                    nint.Zero,
                    0,
                    out _,
                    nint.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set the junction target.");
            }
        }
        catch
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            throw;
        }
    }

    private static byte[] CreateMountPointReparseData(string targetPath)
    {
        var fullTargetPath = Path.GetFullPath(targetPath);
        var substituteName = $"\\??\\{fullTargetPath}";
        var printName = fullTargetPath;
        var substituteNameBytes = Encoding.Unicode.GetBytes(substituteName);
        var printNameBytes = Encoding.Unicode.GetBytes(printName);
        var pathBuffer = new byte[substituteNameBytes.Length + sizeof(char) + printNameBytes.Length + sizeof(char)];

        substituteNameBytes.CopyTo(pathBuffer, 0);
        printNameBytes.CopyTo(pathBuffer, substituteNameBytes.Length + sizeof(char));

        var reparseDataLength = 8 + pathBuffer.Length;
        var reparseData = new byte[8 + reparseDataLength];
        BitConverter.TryWriteBytes(reparseData.AsSpan(0, sizeof(uint)), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(reparseData.AsSpan(sizeof(uint), sizeof(ushort)), (ushort)reparseDataLength);
        BitConverter.TryWriteBytes(reparseData.AsSpan(8, sizeof(ushort)), (ushort)0);
        BitConverter.TryWriteBytes(reparseData.AsSpan(10, sizeof(ushort)), (ushort)substituteNameBytes.Length);
        BitConverter.TryWriteBytes(
            reparseData.AsSpan(12, sizeof(ushort)),
            (ushort)(substituteNameBytes.Length + sizeof(char)));
        BitConverter.TryWriteBytes(reparseData.AsSpan(14, sizeof(ushort)), (ushort)printNameBytes.Length);
        pathBuffer.CopyTo(reparseData, 16);

        return reparseData;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
