using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace WheelSwitcher;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private KeyboardHook? _hook;
    private WheelWindow? _wheel;
    private WindowTracker? _tracker;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _isShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance: refuse to start a second copy (it would double-install the hook).
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Global\WheelSwitcher.SingleInstance",
            out bool createdNew
        );
        if (!createdNew)
        {
            MessageBox.Show(
                "Wheel Switcher is already running (check the system tray).",
                "Wheel Switcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Shutdown();
            return;
        }

        _wheel = new WheelWindow();
        _wheel.OnSlotCommitted += slot => CommitSelection(slot);
        _wheel.OnOverflowCommitted += CommitWindowDirect;
        // Show once at startup so the HWND exists and the window is always in the compositor.
        // It starts at Opacity=0 with WS_EX_TRANSPARENT so it's invisible and click-through.
        _wheel.Show();

        _tracker = new WindowTracker(_wheel.Handle);

        _hook = new KeyboardHook();
        _hook.AltTabPressed += OnAltTab;
        _hook.ShiftTabPressed += OnShiftTab;
        _hook.AltReleased += OnAltReleased;
        _hook.EscapePressed += OnEscape;
        _hook.Install();

        BuildTrayIcon();

        // Pre-warm: build the visual tree while the wheel is still invisible so the
        // first real show is instantaneous (no cold WPF layout pass on the hot path).
        _tracker.Refresh();
        _wheel.PreWarm(_tracker);
    }

    private void BuildTrayIcon()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Wheel Switcher",
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Wheel Switcher").Enabled = false;
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    // ---- Hook events (arrive on the hook thread - marshal to UI) ----

    private void OnAltTab()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isShown)
            {
                _tracker!.Refresh();
                _isShown = true;
                _hook!.WheelActive = true;
                _wheel!.Present(_tracker);
                _wheel!.PreSelectHandle(_tracker.PreviousWindowHandle);
            }
            else
            {
                // Already shown - advance selection (equivalent of repeated Alt+Tab tap).
                _wheel!.AdvanceSelection(1);
            }
        });
    }

    private void OnShiftTab()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isShown)
                _wheel!.AdvanceSelection(-1);
        });
    }

    private void OnAltReleased()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isShown)
                return;
            var overflowTw = _wheel!.SelectedOverflowWindow;
            if (overflowTw is not null)
                CommitWindowDirect(overflowTw);
            else
                CommitSelection(_wheel!.HoverSlot);
        });
    }

    private void OnEscape()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isShown)
                return;
            _isShown = false;
            _hook!.WheelActive = false;
            _wheel!.Dismiss();
            ScheduleReWarm();
        });
    }

    private void CommitWindowDirect(TrackedWindow tw)
    {
        if (!_isShown)
            return;
        _isShown = false;
        _hook!.WheelActive = false;
        _wheel!.Dismiss();
        WindowActivator.Activate(tw.Handle);
        ScheduleReWarm();
    }

    private void CommitSelection(int slot)
    {
        if (!_isShown)
            return;
        _isShown = false;
        _hook!.WheelActive = false;

        if (slot >= 0 && _tracker!.Slots[slot] is TrackedWindow tw)
        {
            _wheel!.Dismiss();
            WindowActivator.Activate(tw.Handle);
        }
        else
        {
            _wheel!.Dismiss();
        }
        ScheduleReWarm();
    }

    private void ScheduleReWarm()
    {
        // Run at background priority so the dismiss render completes first.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () =>
            {
                _tracker!.Refresh();
                _wheel!.PreWarm(_tracker);
            }
        );
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
