using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace B_VIP.Hardware
{
    /// <summary>
    /// Listens for the Windows <c>WM_DEVICECHANGE</c> broadcast that the system sends to every
    /// top-level window when a USB device is plugged in or removed. The message-only windows WMI
    /// uses are not part of that broadcast, so this creates a hidden top-level window on its own
    /// background thread and pumps its messages.
    ///
    /// This is a second, independent path to the same information <see cref="PortWatcherService"/>
    /// already gets from WMI: it costs one thread and keeps detection working on machines where the
    /// WMI event provider is disabled or restricted.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class DeviceChangeWatcher : IDisposable
    {
        private const int WmDeviceChange = 0x0219;
        private const int WmClose = 0x0010;
        private const int WmDestroy = 0x0002;
        private const int WmQuit = 0x0012;
        private const int DbtDevNodesChanged = 0x0007;
        private const int DbtDeviceArrival = 0x8000;
        private const int DbtDeviceRemoveComplete = 0x8004;

        private readonly Action _onChange;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly string _className = $"BVIP_DeviceChange_{Guid.NewGuid():N}";
        private Thread? _thread;
        private WndProcDelegate? _wndProc;
        private IntPtr _window;
        private uint _threadId;
        private bool _running;
        private bool _disposed;

        public DeviceChangeWatcher(Action onChange)
        {
            _onChange = onChange ?? throw new ArgumentNullException(nameof(onChange));
        }

        /// <summary>
        /// Starts the listener and waits up to <paramref name="timeoutMs"/> for the window to exist.
        /// Returns false when Windows would not give us a window; the caller keeps its other
        /// detection paths in that case.
        /// </summary>
        public bool Start(int timeoutMs = 2000)
        {
            if (_disposed || _running || !OperatingSystem.IsWindows())
            {
                return false;
            }

            _thread = new Thread(PumpMessages)
            {
                IsBackground = true,
                Name = "B_VIP USB device notifications"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_ready.Wait(timeoutMs))
            {
                Dispose();
                return false;
            }

            return _running;
        }

        private void PumpMessages()
        {
            _threadId = GetCurrentThreadId();

            try
            {
                _wndProc = WindowProc;

                WndClass wc = new()
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = GetModuleHandle(null),
                    lpszClassName = _className,
                    lpszMenuName = null
                };

                if (RegisterClass(ref wc) == 0)
                {
                    _ready.Set();
                    return;
                }

                // A hidden top-level popup: broadcast messages only reach top-level windows, and the
                // empty style keeps it out of the taskbar and off the screen.
                _window = CreateWindowEx(0, _className, "B_VIP device change listener", 0, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

                if (_window == IntPtr.Zero)
                {
                    _ready.Set();
                    return;
                }

                _running = true;
                _ready.Set();

                while (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                // The caller simply keeps WMI and the polling fallback.
            }
            finally
            {
                _running = false;
                _ready.Set();

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        UnregisterClass(_className, GetModuleHandle(null));
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                    {
                    }
                }
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WmDeviceChange:
                    int change = wParam.ToInt32();
                    if (change is DbtDevNodesChanged or DbtDeviceArrival or DbtDeviceRemoveComplete)
                    {
                        try
                        {
                            _onChange();
                        }
                        catch (Exception)
                        {
                            // A refresh that throws must not take the message pump down with it.
                        }
                    }

                    return IntPtr.Zero;

                case WmClose:
                    DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case WmDestroy:
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                default:
                    return DefWindowProc(hWnd, msg, wParam, lParam);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_window != IntPtr.Zero && OperatingSystem.IsWindows())
                {
                    PostMessage(_window, WmClose, IntPtr.Zero, IntPtr.Zero);
                    _thread?.Join(TimeSpan.FromSeconds(2));
                }

                if (_thread is { IsAlive: true } && _threadId != 0)
                {
                    PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
                    _thread.Join(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            finally
            {
                _window = IntPtr.Zero;
                _running = false;
                _ready.Dispose();
            }
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClass
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref WndClass wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClass(string className, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg msg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
