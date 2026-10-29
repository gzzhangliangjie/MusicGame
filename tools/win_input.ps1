param(
    [Parameter(Mandatory=$true)][string]$cmd,
    [int]$x = 0,
    [int]$y = 0,
    [string]$vk = "",
    [string]$out = "",
    [int]$pid2 = 0
)

# 真实输入/截屏驱动：OS 级鼠标键盘测试用（编辑器 Game 视图 + 独立包通用）
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, out POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@

[W32]::SetProcessDPIAware() | Out-Null

switch ($cmd) {
    "shot" {
        $w = [W32]::GetSystemMetrics(0)
        $h = [W32]::GetSystemMetrics(1)
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
        $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Output "shot=$out $w x $h"
    }
    "restore" {
        $p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
            [W32]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
            Start-Sleep -Milliseconds 300
            [W32]::MoveWindow($p.MainWindowHandle, 0, 0, 1707, 1067, $true) | Out-Null
            Start-Sleep -Milliseconds 500
            Write-Output "restored pid=$pid2"
        } else { Write-Output "restore FAILED" }
    }
    "focus" {
        $p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
            [W32]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
            [W32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
            [W32]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 400
            Write-Output "focused pid=$pid2"
        } else { Write-Output "focus FAILED pid=$pid2" }
    }
    "shotwin" {
        $p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
        if (-not $p -or $p.MainWindowHandle -eq [IntPtr]::Zero) { Write-Output "nowindow"; exit }
        $rc = New-Object W32+RECT
        [W32]::GetWindowRect($p.MainWindowHandle, [ref]$rc) | Out-Null
        $w = $rc.Right - $rc.Left; $h = $rc.Bottom - $rc.Top
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [W32]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null
        $g.ReleaseHdc($hdc); $g.Dispose()
        $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Output "shotwin=$out rect=($($rc.Left),$($rc.Top))-($($rc.Right),$($rc.Bottom))"
    }
    "click" {
        [W32]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 120
        [W32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [W32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Write-Output "click screen($x,$y)"
    }
    "key" {
        $code = [Convert]::ToInt32($vk, 16)
        [W32]::keybd_event([byte]$code, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [W32]::keybd_event([byte]$code, 0, 2, [UIntPtr]::Zero)
        Write-Output "key=0x$vk"
    }
}
