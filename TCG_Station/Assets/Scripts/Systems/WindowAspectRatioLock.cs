using UnityEngine;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
#endif

/// <summary>
/// Locks the standalone Windows player window to a fixed aspect ratio while it
/// stays resizable. The window procedure is sub-classed so WM_SIZING is corrected
/// on every drag, keeping the UI from stretching in windowed mode.
/// No-op in the editor and on non-Windows platforms.
/// </summary>
public class WindowAspectRatioLock : MonoBehaviour
{
    [Tooltip("Target width:height ratio (16:9 = 1.7777...).")]
    public float aspectWidth = 16f;
    public float aspectHeight = 9f;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private const int GWLP_WNDPROC = -4;
    private const uint WM_SIZING = 0x0214;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // WMSZ_* edge codes from the WM_SIZING wParam.
    private const int WMSZ_LEFT = 1;
    private const int WMSZ_RIGHT = 2;
    private const int WMSZ_TOP = 3;
    private const int WMSZ_TOPLEFT = 4;
    private const int WMSZ_TOPRIGHT = 5;
    private const int WMSZ_BOTTOM = 6;
    private const int WMSZ_BOTTOMLEFT = 7;
    private const int WMSZ_BOTTOMRIGHT = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate newProc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CallWindowProc(IntPtr prevProc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr _hWnd;
    private IntPtr _oldWndProc;
    private WndProcDelegate _newWndProc; // kept alive to prevent GC of the delegate
    private float _ratio;

    private void Start()
    {
        _ratio = aspectWidth / aspectHeight;
        _hWnd = GetActiveWindow();
        if (_hWnd == IntPtr.Zero) return;

        _newWndProc = WndProc;
        _oldWndProc = SetWindowLongPtr(_hWnd, GWLP_WNDPROC, _newWndProc);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SIZING)
        {
            RECT rect = (RECT)Marshal.PtrToStructure(lParam, typeof(RECT));
            int edge = wParam.ToInt32();

            int width = rect.right - rect.left;
            int height = rect.bottom - rect.top;

            // When dragging a horizontal edge (top/bottom) drive width from height,
            // otherwise drive height from width.
            bool sizingVertical = edge == WMSZ_TOP || edge == WMSZ_BOTTOM;

            if (sizingVertical)
            {
                width = Mathf.RoundToInt(height * _ratio);
            }
            else
            {
                height = Mathf.RoundToInt(width / _ratio);
            }

            // Anchor the side that is NOT being dragged so the window doesn't jump.
            switch (edge)
            {
                case WMSZ_LEFT:
                case WMSZ_RIGHT:
                    rect.bottom = rect.top + height;
                    break;
                case WMSZ_TOP:
                    rect.left = rect.right - width;
                    break;
                case WMSZ_BOTTOM:
                    rect.right = rect.left + width;
                    break;
                case WMSZ_TOPLEFT:
                    rect.top = rect.bottom - height;
                    rect.left = rect.right - width;
                    break;
                case WMSZ_TOPRIGHT:
                    rect.top = rect.bottom - height;
                    rect.right = rect.left + width;
                    break;
                case WMSZ_BOTTOMLEFT:
                    rect.bottom = rect.top + height;
                    rect.left = rect.right - width;
                    break;
                case WMSZ_BOTTOMRIGHT:
                default:
                    rect.bottom = rect.top + height;
                    rect.right = rect.left + width;
                    break;
            }

            Marshal.StructureToPtr(rect, lParam, true);
        }
        else if (msg == WM_GETMINMAXINFO)
        {
            // Maximize (window fill button) does not send WM_SIZING, so constrain the
            // maximized size to the largest 16:9 box that fits the monitor work area.
            IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(monitor, ref mi))
                {
                    int workW = mi.rcWork.right - mi.rcWork.left;
                    int workH = mi.rcWork.bottom - mi.rcWork.top;

                    int targetW = workW;
                    int targetH = Mathf.RoundToInt(targetW / _ratio);
                    if (targetH > workH)
                    {
                        targetH = workH;
                        targetW = Mathf.RoundToInt(targetH * _ratio);
                    }

                    MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                    // Position is relative to the monitor's own origin; center within the work area.
                    mmi.ptMaxPosition.x = (mi.rcWork.left - mi.rcMonitor.left) + (workW - targetW) / 2;
                    mmi.ptMaxPosition.y = (mi.rcWork.top - mi.rcMonitor.top) + (workH - targetH) / 2;
                    mmi.ptMaxSize.x = targetW;
                    mmi.ptMaxSize.y = targetH;
                    mmi.ptMaxTrackSize.x = targetW;
                    mmi.ptMaxTrackSize.y = targetH;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void OnDestroy()
    {
        // Restore the original window procedure to avoid a dangling pointer.
        if (_hWnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hWnd, GWLP_WNDPROC, Marshal.GetDelegateForFunctionPointer(
                _oldWndProc, typeof(WndProcDelegate)) as WndProcDelegate);
        }
    }
#endif
}
