using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WinUIonWebUWP.ShortcutBridge;

internal static class Program
{
    private const string RequestFileName = "desktop-shortcut-request.json";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || !string.Equals(args[0], "/createShortcut", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var localState = GetPackageLocalStatePath();
            var requestPath = Path.Combine(localState, RequestFileName);
            if (!File.Exists(requestPath))
            {
                return 2;
            }

            var request = JsonSerializer.Deserialize<DesktopShortcutRequest>(File.ReadAllText(requestPath));
            if (request == null
                || string.IsNullOrWhiteSpace(request.ContainerId)
                || string.IsNullOrWhiteSpace(request.DisplayName)
                || string.IsNullOrWhiteSpace(request.LaunchUri))
            {
                return 3;
            }

            var shortcutName = SanitizeFileName(request.DisplayName);
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(desktopPath, shortcutName + ".lnk");
            var iconPath = CreateShortcutIcon(localState, request);
            CreateShortcut(shortcutPath, request.LaunchUri, request.DisplayName, iconPath);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static string GetPackageLocalStatePath()
    {
        var length = 0;
        _ = GetCurrentPackageFamilyName(ref length, null);
        if (length <= 0)
        {
            throw new InvalidOperationException("Package identity is unavailable.");
        }

        var familyName = new StringBuilder(length);
        var result = GetCurrentPackageFamilyName(ref length, familyName);
        if (result != 0)
        {
            throw new InvalidOperationException("Package family name is unavailable.");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            familyName.ToString(),
            "LocalState");
    }

    private static string CreateShortcutIcon(string localState, DesktopShortcutRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IconPngPath) || !File.Exists(request.IconPngPath))
        {
            return "";
        }

        var iconFolder = Path.Combine(localState, "shortcut-icons");
        Directory.CreateDirectory(iconFolder);
        var iconPath = Path.Combine(iconFolder, SanitizeFileName(request.ContainerId) + ".ico");
        WritePngAsIco(request.IconPngPath, iconPath);
        return iconPath;
    }

    private static void WritePngAsIco(string pngPath, string icoPath)
    {
        var pngBytes = File.ReadAllBytes(pngPath);
        var (width, height) = ReadPngSize(pngBytes);

        using var output = File.Create(icoPath);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)(width >= 256 ? 0 : width));
        writer.Write((byte)(height >= 256 ? 0 : height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(pngBytes.Length);
        writer.Write(22);
        writer.Write(pngBytes);
    }

    private static (int Width, int Height) ReadPngSize(byte[] bytes)
    {
        if (bytes.Length >= 24
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47)
        {
            var width = ReadBigEndianInt32(bytes, 16);
            var height = ReadBigEndianInt32(bytes, 20);
            if (width > 0 && height > 0)
            {
                return (width, height);
            }
        }

        return (256, 256);
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset) =>
        (bytes[offset] << 24)
        | (bytes[offset + 1] << 16)
        | (bytes[offset + 2] << 8)
        | bytes[offset + 3];

    private static void CreateShortcut(string shortcutPath, string launchUri, string displayName, string iconPath)
    {
        var shellLink = (IShellLinkW)(object)new CShellLink();
        shellLink.SetPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        shellLink.SetArguments(launchUri);
        shellLink.SetDescription(displayName);
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            shellLink.SetIconLocation(iconPath, 0);
        }

        ((IPersistFile)shellLink).Save(shortcutPath, true);
    }

    private static string SanitizeFileName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Web Container" : value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Length > 80 ? name.Substring(0, 80) : name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder? packageFamilyName);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class CShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(IntPtr pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(IntPtr pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(IntPtr pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(IntPtr pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    private sealed class DesktopShortcutRequest
    {
        public string ContainerId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string LaunchUri { get; set; } = "";
        public string IconPngPath { get; set; } = "";
    }
}
