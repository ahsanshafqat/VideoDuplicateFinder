//using System;
//using System.IO;
//using System.Runtime.InteropServices;

//namespace VideoDuplicateFinder
//{
//    using System;
//    using System.IO;
//    using System.Runtime.InteropServices;

//    public class ExplorerHelper
//    {
//        [StructLayout(LayoutKind.Sequential)]
//        private struct RECT
//        {
//            public int Left, Top, Right, Bottom;
//        }

//        [DllImport("user32.dll")]
//        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

//        [DllImport("user32.dll", SetLastError = true)]
//        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

//        [DllImport("user32.dll", SetLastError = true)]
//        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

//        [DllImport("user32.dll")]
//        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

//        /// <summary>
//        /// Selects ONLY the specified single file in the Explorer window and scrolls directly to it.
//        /// Accepts either just the file name (e.g., "report.pdf") or full path (e.g., @"C:\Docs\report.pdf").
//        /// </summary>
//        public static void SelectFilesInExplorer(IntPtr explorerWindowHandle, string fileNameOrPath)
//        {
//            if (string.IsNullOrWhiteSpace(fileNameOrPath)) return;

//            // Extract just the file name in case a full path was passed
//            string targetFileName = Path.GetFileName(fileNameOrPath);

//            try
//            {
//                const uint GA_ROOT = 2;
//                IntPtr targetRoot = GetAncestor(explorerWindowHandle, GA_ROOT);
//                if (targetRoot == IntPtr.Zero) targetRoot = explorerWindowHandle;

//                Type shellType = Type.GetTypeFromProgID("Shell.Application");
//                dynamic shell = Activator.CreateInstance(shellType);
//                dynamic windows = shell.Windows();

//                for (int i = 0; i < windows.Count; i++)
//                {
//                    dynamic window = windows.Item(i);
//                    if (window == null) continue;

//                    IntPtr windowHwnd = (IntPtr)(long)window.HWND;
//                    IntPtr windowRoot = GetAncestor(windowHwnd, GA_ROOT);
//                    if (windowRoot == IntPtr.Zero) windowRoot = windowHwnd;

//                    if (windowRoot == targetRoot)
//                    {
//                        dynamic folderView = window.Document;
//                        dynamic folder = folderView.Folder;

//                        // Locate the single specific file inside the folder
//                        dynamic item = folder.ParseName(targetFileName);

//                        if (item != null)
//                        {
//                            // SVSI_SELECT (1) | SVSI_DESELECTOTHERS (4) | SVSI_ENSUREVISIBLE (8) | SVSI_FOCUSED (16) = 29
//                            // Selects ONLY this file, clears any other selection, and scrolls it into view
//                            folderView.SelectItem(item, 29);
//                            Marshal.ReleaseComObject(item);
//                        }

//                        Marshal.ReleaseComObject(folder);
//                        Marshal.ReleaseComObject(folderView);
//                        Marshal.ReleaseComObject(window);
//                        break;
//                    }
//                    Marshal.ReleaseComObject(window);
//                }

//                Marshal.ReleaseComObject(windows);
//                Marshal.ReleaseComObject(shell);
//            }
//            catch
//            {
//                // Silently continue if COM interaction fails
//            }
//        }

//        /// <summary>
//        /// Places the source Explorer window on the left 50% and target on the right 50%.
//        /// </summary>
//        public static void ArrangeSideBySide(IntPtr srcHandle, IntPtr tgtHandle)
//        {
//            const uint GA_ROOT = 2;
//            const uint SPI_GETWORKAREA = 0x0030;
//            const uint SWP_NOZORDER = 0x0004;
//            const uint SWP_NOACTIVATE = 0x0010;
//            const uint SWP_ASYNCWINDOWPOS = 0x4000;
//            const int SW_RESTORE = 9;

//            IntPtr srcRoot = GetAncestor(srcHandle, GA_ROOT);
//            IntPtr tgtRoot = GetAncestor(tgtHandle, GA_ROOT);

//            if (srcRoot == IntPtr.Zero) srcRoot = srcHandle;
//            if (tgtRoot == IntPtr.Zero) tgtRoot = tgtHandle;

//            ShowWindow(srcRoot, SW_RESTORE);
//            ShowWindow(tgtRoot, SW_RESTORE);

//            if (SystemParametersInfo(SPI_GETWORKAREA, 0, out RECT workArea, 0))
//            {
//                int totalWidth = workArea.Right - workArea.Left;
//                int height = workArea.Bottom - workArea.Top;
//                int halfWidth = totalWidth / 2;

//                uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;

//                // Source -> Left 50%
//                SetWindowPos(srcRoot, IntPtr.Zero, workArea.Left, workArea.Top, halfWidth, height, flags);

//                // Target -> Right 50%
//                SetWindowPos(tgtRoot, IntPtr.Zero, workArea.Left + halfWidth, workArea.Top, halfWidth, height, flags);
//            }
//        }
//    }

//}
