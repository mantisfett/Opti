using System.Runtime.InteropServices;

namespace Opti.Bench;

/// <summary>Минимальное окно Win32 без WinForms: рамка не нужна, только вывод кадров.</summary>
internal sealed class Win32Window : IDisposable
{
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_KEYDOWN = 0x0100, PM_REMOVE = 1;
    private const int VK_ESCAPE = 0x1B, SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    private static WndProcDelegate? _proc; // держим ссылку, чтобы делегат не собрал GC
    public IntPtr Handle { get; }
    public int Width { get; }
    public int Height { get; }
    public bool Closed { get; private set; }
    public bool Escaped { get; private set; }

    public Win32Window(string title, int width = 0, int height = 0)
    {
        bool fullscreen = width <= 0 || height <= 0;
        Width = fullscreen ? GetSystemMetrics(SM_CXSCREEN) : width;
        Height = fullscreen ? GetSystemMetrics(SM_CYSCREEN) : height;
        _proc = WndProc;
        var hInst = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst,
            hCursor = LoadCursorW(IntPtr.Zero, 32512),
            lpszClassName = "OptiBench",
        };
        RegisterClassExW(ref wc);
        Handle = CreateWindowExW(0, "OptiBench", title, fullscreen ? WS_POPUP | WS_VISIBLE : WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            fullscreen ? 0 : 100, fullscreen ? 0 : 100, Width, Height, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("Не удалось создать окно");
        ShowWindow(Handle, 5);
        SetForegroundWindow(Handle);
        if (fullscreen) _ = ShowCursor(false);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_KEYDOWN when wParam.ToInt32() == VK_ESCAPE:
                Escaped = true;
                Closed = true;
                return IntPtr.Zero;
            case WM_CLOSE:
            case WM_DESTROY:
                Closed = true;
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void PumpMessages()
    {
        while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
    }

    public void Dispose()
    {
        _ = ShowCursor(true);
        DestroyWindow(Handle);
    }
}
