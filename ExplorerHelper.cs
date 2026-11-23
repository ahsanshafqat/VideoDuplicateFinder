using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoDuplicateFinder
{
    public class ExplorerHelper
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        private static HashSet<IntPtr> GetVisibleExplorerWindows()
        {
            var handles = new HashSet<IntPtr>();
            var className = new StringBuilder(64);

            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    className.Clear();
                    GetClassName(hWnd, className, className.Capacity);
                    if (className.ToString() == "CabinetWClass")
                    {
                        handles.Add(hWnd);
                    }
                }
                return true;
            }, IntPtr.Zero);

            return handles;
        }

        private static string NormalizeDir(string fileOrFolderPath)
        {
            if (string.IsNullOrWhiteSpace(fileOrFolderPath)) return string.Empty;
            string dir = Directory.Exists(fileOrFolderPath)
                ? fileOrFolderPath
                : Path.GetDirectoryName(fileOrFolderPath);

            return (dir ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Finds an already-open Explorer window showing targetDir, strictly excluding excludeRoot
        /// so source and target NEVER share the same window handle.
        /// </summary>
        private static IntPtr FindExistingExplorerForFolder(string targetDir, IntPtr excludeRoot)
        {
            if (string.IsNullOrEmpty(targetDir)) return IntPtr.Zero;
            const uint GA_ROOT = 2;
            IntPtr matchedRoot = IntPtr.Zero;

            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic windows = shell.Windows();

            try
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    dynamic window = windows.Item(i);
                    if (window == null) continue;

                    try
                    {
                        IntPtr hwnd = (IntPtr)(long)window.HWND;
                        IntPtr root = GetAncestor(hwnd, GA_ROOT);
                        if (root == IntPtr.Zero) root = hwnd;

                        // Never return the same window already assigned to Source
                        if (root == excludeRoot || !IsWindowVisible(root))
                            continue;

                        dynamic doc = window.Document;
                        dynamic folder = doc?.Folder;
                        dynamic self = folder?.Self;
                        string currentPath = self?.Path as string;

                        if (self != null) Marshal.ReleaseComObject(self);
                        if (folder != null) Marshal.ReleaseComObject(folder);
                        if (doc != null) Marshal.ReleaseComObject(doc);

                        if (!string.IsNullOrEmpty(currentPath))
                        {
                            string normalizedCurrent = currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            if (string.Equals(normalizedCurrent, targetDir, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedRoot = root;
                                break;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        Marshal.ReleaseComObject(window);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(windows);
                Marshal.ReleaseComObject(shell);
            }

            return matchedRoot;
        }

        public static async Task<(bool success, IntPtr srcRoot, IntPtr tgtRoot)> OpenAndArrangeSideBySideAsync(
            string sourcePath,
            string targetPath,
            Control uiOwner)
        {
            const uint GA_ROOT = 2;
            const int SW_RESTORE = 9;

            var work = Screen.FromControl(uiOwner).WorkingArea;
            int halfWidth = work.Width / 2;

            return await Task.Run(async () =>
            {
            string srcDir = NormalizeDir(sourcePath);
            string tgtDir = NormalizeDir(targetPath);

            // 1. Check if an Explorer window is ALREADY open to srcDir
            IntPtr srcRoot = FindExistingExplorerForFolder(srcDir, IntPtr.Zero);
            bool openedNewSrc = false;

            if (srcRoot == IntPtr.Zero)
            {
                var before = GetVisibleExplorerWindows();
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/n,/select,\"{sourcePath}\"",
                    UseShellExecute = true
                });

                for (int attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(25);
                    var diff = GetVisibleExplorerWindows().Except(before).ToList();
                    if (diff.Count >= 1)
                    {
                        srcRoot = diff[0];
                        openedNewSrc = true;
                        break;
                    }
                }
            }

            if (srcRoot == IntPtr.Zero)
                return (false, IntPtr.Zero, IntPtr.Zero);

            // 2. Check if a DIFFERENT Explorer window is ALREADY open to tgtDir (strictly excluding srcRoot)
            IntPtr tgtRoot = FindExistingExplorerForFolder(tgtDir, srcRoot);
            bool openedNewTgt = false;
            bool navigatedTgt = false;

            if (tgtRoot == IntPtr.Zero)
            {
                bool sameFolder = string.Equals(srcDir, tgtDir, StringComparison.OrdinalIgnoreCase);
                var beforeSecond = GetVisibleExplorerWindows();

                if (sameFolder)
                {
                    // Force an independent second window via This PC CLSID so Windows 11 doesn't reuse srcRoot
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/n,::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
                        UseShellExecute = true
                    });
                    navigatedTgt = true;
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/n,/select,\"{targetPath}\"",
                        UseShellExecute = true
                    });
                }

                for (int attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(25);
                    var diff = GetVisibleExplorerWindows().Except(beforeSecond).ToList();
                    if (diff.Count >= 1)
                    {
                        tgtRoot = diff[0];
                        openedNewTgt = true;
                        break;
                    }
                }
            }

            if (tgtRoot == IntPtr.Zero || tgtRoot == srcRoot)
                return (false, IntPtr.Zero, IntPtr.Zero);

            // Restore if either reused window was minimized or maximized
            ShowWindow(srcRoot, SW_RESTORE);
            ShowWindow(tgtRoot, SW_RESTORE);

            // 3. Position 50/50 side-by-side and update file selection/scroll
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic windows = shell.Windows();

            dynamic srcWindow = null;
            dynamic tgtWindow = null;

            try
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    dynamic window = windows.Item(i);
                    if (window == null) continue;

                        IntPtr hwnd = (IntPtr)(long)window.HWND;
                        IntPtr root = GetAncestor(hwnd, GA_ROOT);
                        if (root == IntPtr.Zero) root = hwnd;
                        if (root == srcRoot)
                        {
                            window.Left = work.Left;
                            window.Top = work.Top;
                            window.Width = halfWidth;
                            window.Height = work.Height;
                            srcWindow = window;
                        }
                        else if (root == tgtRoot)
                        {
                            window.Left = work.Left + halfWidth;
                            window.Top = work.Top;
                            window.Width = halfWidth;
                            window.Height = work.Height;
                            tgtWindow = window;
                        }
                        else
                        {
                            Marshal.ReleaseComObject(window);
                        }
                    }
                    if (navigatedTgt && tgtWindow != null && Directory.Exists(tgtDir))
                    {
                        tgtWindow.Navigate2(tgtDir);
                        await Task.Delay(250);
                    }
                    else if (openedNewSrc || openedNewTgt)
                    {
                        await Task.Delay(180);
                    }
                    // Always update selection & scroll to the newly requested source/target files
                    ForceScrollToFile(srcWindow, sourcePath);
                    ForceScrollToFile(tgtWindow, targetPath);
                }
                finally
                {
                    if (srcWindow != null) Marshal.ReleaseComObject(srcWindow);
                    if (tgtWindow != null) Marshal.ReleaseComObject(tgtWindow);
                    Marshal.ReleaseComObject(windows);
                    Marshal.ReleaseComObject(shell);
                }
                return (true, srcRoot, tgtRoot);
            });
        }
        private static void ForceScrollToFile(dynamic window, string fullFilePath)
        {
            if (window == null || string.IsNullOrWhiteSpace(fullFilePath)) return;
            try
            {
                string fileName = Path.GetFileName(fullFilePath);
                dynamic folderView = window.Document;
                dynamic folder = folderView.Folder;
                dynamic item = folder.ParseName(fileName);
                if (item != null)
                {
                    // Deselect previous file (4), then select + ensure visible + focus new file (25)
                    folderView.SelectItem(item, 4);
                    folderView.SelectItem(item, 25);
                    Marshal.ReleaseComObject(item);
                }
                Marshal.ReleaseComObject(folder);
                Marshal.ReleaseComObject(folderView);
            }
            catch { }
        }
        /// 
        /// Brings both Explorer windows above the application and gives keyboard focus to the source window.
        /// Must be called on the UI thread.
        /// 
        public static void BringExplorersToFront(IntPtr srcRoot, IntPtr tgtRoot)
        {
            if (srcRoot == IntPtr.Zero || tgtRoot == IntPtr.Zero) return;
            uint currentThread = GetCurrentThreadId();
            uint srcThread = GetWindowThreadProcessId(srcRoot, out _);
            uint tgtThread = GetWindowThreadProcessId(tgtRoot, out _);
            try
            {
                if (tgtThread != currentThread) AttachThreadInput(currentThread, tgtThread, true);
                if (srcThread != currentThread && srcThread != tgtThread) AttachThreadInput(currentThread, srcThread, true);
                // Raise target window first so it sits on the right half above your app
                ShowWindow(tgtRoot, 9); // SW_RESTORE
                BringWindowToTop(tgtRoot);
                SetForegroundWindow(tgtRoot);
                // Raise source window second so it sits on the left half AND holds active keyboard focus
                ShowWindow(srcRoot, 9); // SW_RESTORE
                BringWindowToTop(srcRoot);
                SetForegroundWindow(srcRoot);
            }
            finally
            {
                if (tgtThread != currentThread) AttachThreadInput(currentThread, tgtThread, false);
                if (srcThread != currentThread && srcThread != tgtThread) AttachThreadInput(currentThread, srcThread, false);
            }
        }
    }
}