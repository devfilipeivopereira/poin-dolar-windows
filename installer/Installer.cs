using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PoinDolarWindowsInstaller
{
    internal static class InstallerProgram
    {
        private const string AppName = "Poin Dolar Windows";
        private const string AppKey = "PoinDolarWindows";
        private const string PayloadResourceName = "PoinDolarPayload.zip";

        [STAThread]
        private static int Main(string[] args)
        {
            bool quiet = HasArg(args, "--quiet");
            string installDir = GetArgValue(args, "--installDir=");

            if (string.IsNullOrWhiteSpace(installDir))
            {
                installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppKey);
            }

            try
            {
                if (HasArg(args, "--uninstall"))
                {
                    Uninstall(installDir, quiet);
                    return 0;
                }

                Install(
                    installDir,
                    quiet,
                    HasArg(args, "--no-shortcuts"),
                    HasArg(args, "--no-registry"),
                    HasArg(args, "--launch"));

                return 0;
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    MessageBox.Show(
                        ex.GetType().Name + ": " + ex.Message,
                        AppName + " - instalador",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                return 1;
            }
        }

        private static void Install(string installDir, bool quiet, bool noShortcuts, bool noRegistry, bool launch)
        {
            Directory.CreateDirectory(installDir);
            ExtractPayload(installDir);
            CopySelfForUninstall(installDir);

            string appPath = GetPreferredAppPath(installDir);

            if (!noShortcuts)
            {
                CreateShortcuts(installDir, appPath);
            }

            if (!noRegistry)
            {
                RegisterUninstall(installDir);
            }

            if (launch && File.Exists(appPath))
            {
                Process.Start(appPath);
            }

            if (!quiet)
            {
                MessageBox.Show(
                    AppName + " foi instalado em:" + Environment.NewLine + installDir,
                    AppName + " - instalador",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private static void Uninstall(string installDir, bool quiet)
        {
            RemoveShortcuts();
            RemoveRegistry();

            string currentExe = Application.ExecutablePath;
            bool runningInsideInstallDir = IsPathInside(currentExe, installDir);

            if (Directory.Exists(installDir))
            {
                if (runningInsideInstallDir)
                {
                    string args = "/c ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"" + installDir + "\"";
                    Process.Start(new ProcessStartInfo("cmd.exe", args)
                    {
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                }
                else
                {
                    Directory.Delete(installDir, true);
                }
            }

            if (!quiet)
            {
                MessageBox.Show(
                    AppName + " foi removido.",
                    AppName + " - instalador",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private static void ExtractPayload(string installDir)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();

            using (Stream stream = assembly.GetManifestResourceStream(PayloadResourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Payload embutido nao encontrado: " + PayloadResourceName);
                }

                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string destinationPath = Path.GetFullPath(Path.Combine(installDir, entry.FullName));

                        if (!IsPathInside(destinationPath, installDir))
                        {
                            throw new InvalidOperationException("Entrada invalida no pacote: " + entry.FullName);
                        }

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destinationPath);
                            continue;
                        }

                        string directory = Path.GetDirectoryName(destinationPath);

                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        if (ShouldPreserveExistingFile(entry.FullName, destinationPath))
                        {
                            continue;
                        }

                        entry.ExtractToFile(destinationPath, true);
                    }
                }
            }
        }

        private static bool ShouldPreserveExistingFile(string entryName, string destinationPath)
        {
            if (!File.Exists(destinationPath))
            {
                return false;
            }

            string normalized = entryName.Replace('\\', '/');
            return normalized.EndsWith("/appsettings.json", StringComparison.OrdinalIgnoreCase);
        }

        private static void CopySelfForUninstall(string installDir)
        {
            string source = Application.ExecutablePath;
            string destination = Path.Combine(installDir, "PoinDolarWindowsSetup.exe");

            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, destination, true);
            }
        }

        private static string GetPreferredAppPath(string installDir)
        {
            string x64 = Path.Combine(installDir, "app", "x64", "RtdDolarNative.exe");
            string x86 = Path.Combine(installDir, "app", "x86", "RtdDolarNative.exe");

            if (Environment.Is64BitOperatingSystem && File.Exists(x64))
            {
                return x64;
            }

            if (File.Exists(x86))
            {
                return x86;
            }

            return x64;
        }

        private static void CreateShortcuts(string installDir, string appPath)
        {
            string startMenuFolder = GetStartMenuFolder();
            Directory.CreateDirectory(startMenuFolder);

            CreateShortcut(
                Path.Combine(startMenuFolder, AppName + ".lnk"),
                appPath,
                Path.GetDirectoryName(appPath),
                appPath,
                "Abrir " + AppName);

            string x86 = Path.Combine(installDir, "app", "x86", "RtdDolarNative.exe");

            if (File.Exists(x86))
            {
                CreateShortcut(
                    Path.Combine(startMenuFolder, AppName + " (x86 fallback).lnk"),
                    x86,
                    Path.GetDirectoryName(x86),
                    x86,
                    "Abrir " + AppName + " em x86");
            }

            string uninstallExe = Path.Combine(installDir, "PoinDolarWindowsSetup.exe");
            CreateShortcut(
                Path.Combine(startMenuFolder, "Desinstalar " + AppName + ".lnk"),
                uninstallExe,
                installDir,
                uninstallExe,
                "Desinstalar " + AppName,
                "--uninstall");

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(
                Path.Combine(desktop, AppName + ".lnk"),
                appPath,
                Path.GetDirectoryName(appPath),
                appPath,
                "Abrir " + AppName);
        }

        private static void CreateShortcut(
            string shortcutPath,
            string targetPath,
            string workingDirectory,
            string iconPath,
            string description,
            string arguments = "")
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType == null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = iconPath + ",0";
            shortcut.Description = description;
            shortcut.Save();
        }

        private static void RegisterUninstall(string installDir)
        {
            string uninstallExe = Path.Combine(installDir, "PoinDolarWindowsSetup.exe");
            string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppKey;

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", "0.1.0");
                key.SetValue("Publisher", "devfilipeivopereira");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", uninstallExe);
                key.SetValue("UninstallString", "\"" + uninstallExe + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void RemoveShortcuts()
        {
            DeleteIfExists(Path.Combine(GetStartMenuFolder(), AppName + ".lnk"));
            DeleteIfExists(Path.Combine(GetStartMenuFolder(), AppName + " (x86 fallback).lnk"));
            DeleteIfExists(Path.Combine(GetStartMenuFolder(), "Desinstalar " + AppName + ".lnk"));

            string folder = GetStartMenuFolder();

            if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
            {
                Directory.Delete(folder);
            }

            DeleteIfExists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                AppName + ".lnk"));
        }

        private static void RemoveRegistry()
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppKey,
                false);
        }

        private static string GetStartMenuFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                AppName);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static bool HasArg(string[] args, string expected)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetArgValue(string[] args, string prefix)
        {
            foreach (string arg in args)
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(prefix.Length).Trim('"');
                }
            }

            return null;
        }

        private static bool IsPathInside(string path, string directory)
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }
}
