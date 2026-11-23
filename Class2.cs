using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VideoDuplicateFinder
{



    public class ExplorerHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        /// <summary>
        /// Selects a single file and scrolls to it inside an existing Explorer window via IShellFolderViewDual.
        /// </summary>
        public static void SelectSingleFileInWindow(IntPtr explorerHandle, string fullFilePath)
        {
            if (string.IsNullOrWhiteSpace(fullFilePath)) return;
            string fileName = Path.GetFileName(fullFilePath);

            try
            {
                const uint GA_ROOT = 2;
                IntPtr targetRoot = GetAncestor(explorerHandle, GA_ROOT);
                if (targetRoot == IntPtr.Zero) targetRoot = explorerHandle;

                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic windows = shell.Windows();

                for (int i = 0; i < windows.Count; i++)
                {
                    dynamic window = windows.Item(i);
                    if (window == null) continue;

                    IntPtr windowHwnd = (IntPtr)(long)window.HWND;
                    IntPtr windowRoot = GetAncestor(windowHwnd, GA_ROOT);
                    if (windowRoot == IntPtr.Zero) windowRoot = windowHwnd;

                    if (windowRoot == targetRoot)
                    {
                        dynamic folderView = window.Document;
                        dynamic folder = folderView.Folder;
                        dynamic item = folder.ParseName(fileName);

                        if (item != null)
                        {
                            // 29 = SVSI_SELECT (1) | SVSI_DESELECTOTHERS (4) | SVSI_ENSUREVISIBLE (8) | SVSI_FOCUSED (16)
                            folderView.SelectItem(item, 29);
                            Marshal.ReleaseComObject(item);
                        }

                        Marshal.ReleaseComObject(folder);
                        Marshal.ReleaseComObject(folderView);
                        Marshal.ReleaseComObject(window);
                        break;
                    }
                    Marshal.ReleaseComObject(window);
                }

                Marshal.ReleaseComObject(windows);
                Marshal.ReleaseComObject(shell);
            }
            catch
            {
                // Ignore COM lookup errors
            }
        }

        /// <summary>
        /// Positions two separate Explorer windows flush 50/50 side-by-side, compensating for Win11 invisible borders.
        /// </summary>
        public static void PositionFlushSideBySide(IntPtr srcHandle, IntPtr tgtHandle)
        {
            const uint GA_ROOT = 2;
            const uint MONITOR_DEFAULTTONEAREST = 2;
            const uint SWP_NOZORDER = 0x0004;
            const uint SWP_SHOWWINDOW = 0x0040;
            const int SW_RESTORE = 9;
            const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

            IntPtr srcRoot = GetAncestor(srcHandle, GA_ROOT);
            IntPtr tgtRoot = GetAncestor(tgtHandle, GA_ROOT);
            if (srcRoot == IntPtr.Zero) srcRoot = srcHandle;
            if (tgtRoot == IntPtr.Zero) tgtRoot = tgtHandle;

            // Ensure neither window is maximized or minimized
            ShowWindow(srcRoot, SW_RESTORE);
            ShowWindow(tgtRoot, SW_RESTORE);
            Thread.Sleep(50);

            IntPtr hMonitor = MonitorFromWindow(srcRoot, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(hMonitor, ref monitorInfo)) return;

            RECT workArea = monitorInfo.rcWork;
            int halfWidth = workArea.Width / 2;
            int fullHeight = workArea.Height;

            // Calculate Windows 11 invisible drop-shadow border offset so windows sit flush
            int borderLeft = 0, borderRight = 0, borderBottom = 0;
            if (GetWindowRect(srcRoot, out RECT winRect) &&
                DwmGetWindowAttribute(srcRoot, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT visibleRect, Marshal.SizeOf(typeof(RECT))) == 0)
            {
                borderLeft = visibleRect.Left - winRect.Left;
                borderRight = winRect.Right - visibleRect.Right;
                borderBottom = winRect.Bottom - visibleRect.Bottom;
            }

            // Source -> Left 50%
            SetWindowPos(
                srcRoot,
                IntPtr.Zero,
                workArea.Left - borderLeft,
                workArea.Top,
                halfWidth + borderLeft + borderRight,
                fullHeight + borderBottom,
                SWP_NOZORDER | SWP_SHOWWINDOW);

            // Target -> Right 50%
            SetWindowPos(
                tgtRoot,
                IntPtr.Zero,
                workArea.Left + halfWidth - borderLeft,
                workArea.Top,
                halfWidth + borderLeft + borderRight,
                fullHeight + borderBottom,
                SWP_NOZORDER | SWP_SHOWWINDOW);

            SetForegroundWindow(srcRoot);
        }
    }

}
