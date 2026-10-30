[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Desktop resolution changes are restricted to disposable GitHub-hosted CI runners.'
}

# Use User32 directly. Server Core's Set-DisplayResolution is absent on Desktop Experience runners.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Ci
{
    public static class Desktop
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DisplayMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public uint ICMMethod, ICMIntent, MediaType, DitherType;
            public uint Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumDisplaySettings(string device, int modeNumber, ref DisplayMode mode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettings(ref DisplayMode mode, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, out Rect area, uint flags);

        public static DisplayMode CurrentMode()
        {
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf(typeof(DisplayMode)) };
            const int currentSettings = -1;
            if (!EnumDisplaySettings(null, currentSettings, ref mode))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the primary display mode.");
            return mode;
        }

        public static void SetResolution(uint width, uint height)
        {
            var mode = CurrentMode();
            const uint pelsWidth = 0x80000;
            const uint pelsHeight = 0x100000;
            mode.Fields = pelsWidth | pelsHeight;
            mode.PelsWidth = width;
            mode.PelsHeight = height;
            // Flags zero changes only this session, without persisting a registry display configuration.
            int result = ChangeDisplaySettings(ref mode, 0);
            if (result != 0)
                throw new InvalidOperationException("ChangeDisplaySettings failed with result " + result + ".");
        }

        public static Rect WorkingArea()
        {
            const uint getWorkArea = 0x30;
            Rect area;
            if (!SystemParametersInfo(getWorkArea, 0, out area, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the primary desktop work area.");
            return area;
        }
    }
}
'@

[OpenClaw.Ci.Desktop]::SetResolution(1920, 1080)
$mode = [OpenClaw.Ci.Desktop]::CurrentMode()
$area = [OpenClaw.Ci.Desktop]::WorkingArea()
$workingWidth = $area.Right - $area.Left
$workingHeight = $area.Bottom - $area.Top
$receipt = @{
    width = $mode.PelsWidth
    height = $mode.PelsHeight
    workingArea = @{ left = $area.Left; top = $area.Top; width = $workingWidth; height = $workingHeight }
}
New-Item -ItemType Directory -Force (Join-Path $env:GITHUB_WORKSPACE 'TestResults\Desktop') | Out-Null
$receipt | ConvertTo-Json -Depth 3 | Tee-Object -FilePath (Join-Path $env:GITHUB_WORKSPACE 'TestResults\Desktop\geometry.json')
if ($workingWidth -lt 1400 -or $workingHeight -lt 1000) {
    throw "UI desktop cannot accommodate the required 1300x950 window: ${workingWidth}x${workingHeight}."
}
