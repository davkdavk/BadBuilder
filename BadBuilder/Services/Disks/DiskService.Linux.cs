using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BadBuilder.Services.Disks;

internal static partial class DiskService
{
    private static List<DiskInfo> EnumerateDisksLinux()
    {
        return ReadLinuxDevices()
            .Where(device =>
                device.Type == "disk" &&
                !IsVirtualDisk(device.Name) &&
                (device.Removable || device.Transport.Equals("usb", StringComparison.OrdinalIgnoreCase)))
            .Select(device => new DiskInfo(
                ID: device.Path,
                Name: $"{(string.IsNullOrWhiteSpace(device.Model) ? Path.GetFileName(device.Path) : device.Model.Trim())} ({device.Path})",
                Size: device.Size,
                Type: device.Removable || device.Transport.Equals("usb", StringComparison.OrdinalIgnoreCase)
                    ? DriveType.Removable
                    : DriveType.Fixed,
                DevicePath: device.Path))
            .ToList();
    }

    private static RawDiskStream OpenRawDiskForWriteLinux(DiskInfo disk)
    {
        LinuxBlockDevice? device = ReadLinuxDevices(disk.DevicePath).FirstOrDefault();
        if (device is null || device.Type != "disk")
            throw new IOException($"'{disk.DevicePath}' is no longer an available disk.");

        if (device.Size != disk.Size ||
            (!device.Removable && !device.Transport.Equals("usb", StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"'{disk.DevicePath}' no longer matches the selected removable USB disk.");

        UnmountLinuxDevices(device);

        try
        {
            FileStream stream = new(disk.DevicePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return new RawDiskStream(stream, disk.Size);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Could not open {disk.DevicePath} for writing. Run BadBuilder as root.", ex);
        }
    }

    private static string ReassignLinux(DiskInfo disk)
    {
        using (FileStream device = new(disk.DevicePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (ioctl(device.SafeFileHandle.DangerousGetHandle().ToInt32(), BLKRRPART) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new IOException($"Could not refresh the partition table for {disk.DevicePath} (errno {error}).");
            }
        }

        LinuxBlockDevice? partition = null;
        for (int attempt = 0; attempt < 20 && partition is null; attempt++)
        {
            LinuxBlockDevice? refreshed = ReadLinuxDevices(disk.DevicePath).FirstOrDefault();
            partition = refreshed is null ? null : Flatten(refreshed).FirstOrDefault(item => item.Type == "part");

            if (partition is null)
                Thread.Sleep(250);
        }

        if (partition is null)
            throw new IOException($"The new partition on {disk.DevicePath} did not appear.");

        LinuxBlockDevice refreshedDevice = ReadLinuxDevices(disk.DevicePath).FirstOrDefault()
            ?? throw new IOException($"Could not reread {disk.DevicePath} after formatting.");
        UnmountLinuxDevices(refreshedDevice);

        string diskName = Path.GetFileName(disk.DevicePath);
        string mountPoint = Path.Combine("/mnt", $"BadBuilder-{diskName}");
        if (Directory.Exists(mountPoint) && Directory.EnumerateFileSystemEntries(mountPoint).Any())
            mountPoint += $"-{Guid.NewGuid():N}";

        Directory.CreateDirectory(mountPoint);
        try
        {
            RunLinuxCommand("mount", "-t", "vfat", "--", partition.Path, mountPoint);
            return mountPoint;
        }
        catch
        {
            if (!Directory.EnumerateFileSystemEntries(mountPoint).Any())
                Directory.Delete(mountPoint);
            throw;
        }
    }

    private static List<LinuxBlockDevice> ReadLinuxDevices(string? devicePath = null)
    {
        List<string> arguments = ["--json", "--bytes", "--paths", "--output", "NAME,PATH,SIZE,TYPE,TRAN,RM,MODEL,MOUNTPOINTS"];
        if (devicePath is not null)
            arguments.Add(devicePath);

        string output = RunLinuxCommand("lsblk", [..arguments]);
        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("blockdevices")
            .EnumerateArray()
            .Select(ParseLinuxDevice)
            .ToList();
    }

    private static LinuxBlockDevice ParseLinuxDevice(JsonElement element)
    {
        string name = GetString(element, "name") ?? "unknown";
        string path = GetString(element, "path") ?? $"/dev/{name}";
        long size = element.TryGetProperty("size", out JsonElement sizeValue) && sizeValue.ValueKind == JsonValueKind.Number
            ? sizeValue.GetInt64()
            : 0;
        string type = GetString(element, "type") ?? string.Empty;
        string transport = GetString(element, "tran") ?? string.Empty;
        string model = GetString(element, "model") ?? string.Empty;
        bool removable = element.TryGetProperty("rm", out JsonElement removableValue) &&
            (removableValue.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => removableValue.GetInt32() != 0,
                JsonValueKind.String => removableValue.GetString() == "1",
                _ => false
            });

        List<string> mountPoints = [];
        if (element.TryGetProperty("mountpoints", out JsonElement mountPointValues) && mountPointValues.ValueKind == JsonValueKind.Array)
        {
            mountPoints.AddRange(mountPointValues.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        List<LinuxBlockDevice> children = element.TryGetProperty("children", out JsonElement childValues) && childValues.ValueKind == JsonValueKind.Array
            ? childValues.EnumerateArray().Select(ParseLinuxDevice).ToList()
            : [];

        return new LinuxBlockDevice(name, path, size, type, transport, removable, model, mountPoints, children);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<LinuxBlockDevice> Flatten(LinuxBlockDevice device)
    {
        yield return device;
        foreach (LinuxBlockDevice child in device.Children)
        foreach (LinuxBlockDevice descendant in Flatten(child))
            yield return descendant;
    }

    private static void UnmountLinuxDevices(LinuxBlockDevice device)
    {
        foreach (var (blockDevice, mountPoint) in Flatten(device)
                     .SelectMany(item => item.MountPoints.Select(mountPoint => (BlockDevice: item, MountPoint: mountPoint)))
                     .DistinctBy(entry => entry.MountPoint, StringComparer.Ordinal)
                     .OrderByDescending(entry => entry.MountPoint.Length))
        {
            if (mountPoint == "[SWAP]")
                RunLinuxCommand("swapoff", "--", blockDevice.Path);
            else
                RunLinuxCommand("umount", "--", mountPoint);
        }
    }

    private static bool IsVirtualDisk(string name) =>
        name.StartsWith("loop", StringComparison.Ordinal) ||
        name.StartsWith("ram", StringComparison.Ordinal) ||
        name.StartsWith("zram", StringComparison.Ordinal);

    private static string RunLinuxCommand(string fileName, params string[] arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new IOException($"Failed to start '{fileName}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException($"'{fileName}' failed ({process.ExitCode}): {stderr.Trim()}");

        return stdout;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fileDescriptor, uint request);

    private const uint BLKRRPART = 0x125f;

    private sealed record LinuxBlockDevice(
        string Name,
        string Path,
        long Size,
        string Type,
        string Transport,
        bool Removable,
        string Model,
        IReadOnlyList<string> MountPoints,
        IReadOnlyList<LinuxBlockDevice> Children);
}
