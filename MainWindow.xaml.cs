using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using Microsoft.Win32;

namespace SmartAutoClicker
{
    public partial class MainWindow : Window
    {
        private bool running = false;
        private bool recording = false;

        private int totalClicks = 0;
        private int clicksThisSecond = 0;

        private HwndSource? source;

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        class MacroAction
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Delay { get; set; }
            public bool IsMove { get; set; }
        }

        private List<MacroAction> macro = new List<MacroAction>();

        // WIN API
        [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        struct POINT { public int X; public int Y; }

        const int LEFTDOWN = 0x02;
        const int LEFTUP = 0x04;
        const int RIGHTDOWN = 0x08;
        const int RIGHTUP = 0x10;
        const int MIDDLEDOWN = 0x20;
        const int MIDDLEUP = 0x40;

        const int VK_LBUTTON = 0x01;
        const int HOTKEY_ID = 9000;
        const uint VK_F6 = 0x75;

        public MainWindow()
        {
            InitializeComponent();

            // CPS updater
            Task.Run(() =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                int lastClicks = 0;

                while (true)
                {
                    Thread.Sleep(100);

                    int current = totalClicks;
                    int cps = (int)((current - lastClicks) * (1000.0 / sw.ElapsedMilliseconds));

                    lastClicks = current;
                    sw.Restart();

                    Dispatcher.Invoke(() =>
                    {
                        CpsText.Text = cps.ToString();
                        TotalClicksText.Text = totalClicks.ToString();
                    });
                }
            });
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var helper = new WindowInteropHelper(this);
            source = HwndSource.FromHwnd(helper.Handle);
            source?.AddHook(HwndHook);

            RegisterHotKey(helper.Handle, HOTKEY_ID, 0, VK_F6);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleClicker();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // PICK POSITION
        private void PickButton_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Click anywhere...";

            Task.Run(() =>
            {
                while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
                    Thread.Sleep(1);

                GetCursorPos(out POINT p);

                Dispatcher.Invoke(() =>
                {
                    XBox.Text = p.X.ToString();
                    YBox.Text = p.Y.ToString();
                    StatusText.Text = $"Picked: {p.X}, {p.Y}";
                });
            });
        }

        // CLICKER
        private void StartButton_Click(object sender, RoutedEventArgs e) => ToggleClicker();

        private void ToggleClicker()
        {
            running = !running;

            if (running)
            {
                int interval = int.Parse(IntervalBox.Text);
                int burst = int.Parse(BurstCountBox.Text);
                int delay = int.Parse(BurstDelayBox.Text);

                int x = int.Parse(XBox.Text);
                int y = int.Parse(YBox.Text);

                string button = (ButtonBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Left";
                string location = (LocationBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Mouse";

                Task.Run(() => Loop(interval, burst, delay, x, y, button, location));
                StatusText.Text = "Running";
            }
            else
            {
                StatusText.Text = "Stopped";
            }
        }

        private void Loop(int interval, int burst, int delay, int x, int y, string button, string location)
        {
            var stopwatch = Stopwatch.StartNew();
            long nextClickTime = 0;

            while (running)
            {
                long now = stopwatch.ElapsedMilliseconds;

                if (now >= nextClickTime)
                {
                    if (location.Contains("Fixed"))
                        SetCursorPos(x, y);

                    for (int i = 0; i < burst; i++)
                    {
                        Click(button);

                        var spin = Stopwatch.StartNew();
                        while (spin.ElapsedMilliseconds < delay) { }
                    }

                    nextClickTime += interval;

                    if (now > nextClickTime)
                        nextClickTime = now;
                }
            }
        }

        private void Click(string button)
        {
            switch (button)
            {
                case "Right":
                    mouse_event(RIGHTDOWN, 0, 0, 0, 0);
                    mouse_event(RIGHTUP, 0, 0, 0, 0);
                    break;
                case "Middle":
                    mouse_event(MIDDLEDOWN, 0, 0, 0, 0);
                    mouse_event(MIDDLEUP, 0, 0, 0, 0);
                    break;
                default:
                    mouse_event(LEFTDOWN, 0, 0, 0, 0);
                    mouse_event(LEFTUP, 0, 0, 0, 0);
                    break;
            }

            totalClicks++;
            clicksThisSecond++;
        }

        // MACRO
        private void StartRecording_Click(object sender, RoutedEventArgs e)
        {
            macro.Clear();
            recording = true;
            StatusText.Text = "Recording...";
            Task.Run(() => RecordLoop());
        }

        private void StopRecording_Click(object sender, RoutedEventArgs e)
        {
            recording = false;
            StatusText.Text = $"Recorded {macro.Count}";
        }

        private void RecordLoop()
        {
            int last = Environment.TickCount;

            while (recording)
            {
                int now = Environment.TickCount;

                GetCursorPos(out POINT p);

                macro.Add(new MacroAction
                {
                    X = p.X,
                    Y = p.Y,
                    Delay = now - last,
                    IsMove = true
                });

                last = now;

                Thread.Sleep(10);
            }
        }

        private void PlayMacro_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                foreach (var a in macro)
                {
                    if (a.IsMove)
                        SetCursorPos(a.X, a.Y);

                    Thread.Sleep(a.Delay);
                }
            });
        }

        private void SaveMacro_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dlg = new SaveFileDialog();

            if (dlg.ShowDialog() == true)
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(macro));
        }

        private void LoadMacro_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();

            if (dlg.ShowDialog() == true)
            {
                var loaded = JsonSerializer.Deserialize<List<MacroAction>>(File.ReadAllText(dlg.FileName));
                if (loaded != null)
                    macro = loaded;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);
            source?.RemoveHook(HwndHook);
            base.OnClosed(e);
        }
    }
}