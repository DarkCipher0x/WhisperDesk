using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WhisperDesk;

/// <summary>Scheduled (e.g. overnight) bulk transcription, and keeping the PC awake while jobs run.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private DateTime _scheduledAt;
    private bool _sleepAfterSchedule;

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    /// <summary>Stops Windows from sleeping while a job or live session runs (the screen may still turn off).</summary>
    private static void KeepAwake(bool on) => SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous);

    private void InitSchedule()
    {
        _scheduleTimer.Tick += async (_, _) =>
        {
            if (DateTime.Now < _scheduledAt || _cts != null || LiveRunning) return;
            _scheduleTimer.Stop();
            UpdateScheduleText();
            StatusText.Text = "Scheduled transcription started.";
            await StartJobsAsync(null);
            if (_sleepAfterSchedule && _cts == null && !LiveRunning)
            {
                StatusText.Text = "Scheduled transcription finished. Putting the PC to sleep…";
                await Task.Delay(TimeSpan.FromSeconds(30));   // a moment to cancel by moving the mouse / closing
                if (!LiveRunning && _cts == null) SetSuspendState(false, false, false);
            }
        };
    }

    private void OnSchedule(object sender, RoutedEventArgs e)
    {
        var waiting = _queue.Count(i => i.State != ItemState.Done);
        var day = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
        for (int d = 0; d < 7; d++)
            day.Items.Add(new ComboBoxItem { Content = d switch { 0 => "Today", 1 => "Tomorrow", _ => DateTime.Today.AddDays(d).ToString("dddd d MMM") }, Tag = d });
        var start = _scheduleTimer.IsEnabled ? _scheduledAt : DateTime.Today.AddHours(DateTime.Now.Hour >= 22 ? 24 + 1 : 23);
        day.SelectedIndex = Math.Clamp((start.Date - DateTime.Today).Days, 0, 6);
        var time = new TextBox { Text = start.ToString("HH:mm"), Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 4, 0, 10) };
        var sleep = new CheckBox { Content = "Put the PC to sleep when everything is done", IsChecked = _sleepAfterSchedule, Margin = new Thickness(0, 0, 0, 12) };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = $"Transcribe the {waiting} waiting file(s) later with the current model and options. WhisperDesk must stay open " +
                   "(it can be minimised) and the PC must be awake; while the job runs, the PC is kept from sleeping.",
        });
        panel.Children.Add(new TextBlock { Text = "Day" });
        panel.Children.Add(day);
        panel.Children.Add(new TextBlock { Text = "Time (24-hour, e.g. 23:30)" });
        panel.Children.Add(time);
        panel.Children.Add(sleep);
        var win = SpeakerDialogs.MakeWindow(this, "Schedule transcription", panel, out var ok, "Schedule");
        if (_scheduleTimer.IsEnabled)
        {
            var cancel = new Button { Content = "Remove schedule" };
            cancel.Click += (_, _) => { _scheduleTimer.Stop(); UpdateScheduleText(); win.DialogResult = false; };
            ((StackPanel)panel.Children[^1]).Children.Insert(0, cancel);
        }
        ok.Click += (_, _) =>
        {
            if (!TimeSpan.TryParse(time.Text.Trim(), out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
            {
                MessageBox.Show(win, "Enter a time like 23:30.", "Schedule");
                return;
            }
            var at = DateTime.Today.AddDays((int)((ComboBoxItem)day.SelectedItem).Tag) + t;
            if (at <= DateTime.Now.AddSeconds(30))
            {
                MessageBox.Show(win, "That time has already passed. Pick a later time, or use Transcribe to start now.", "Schedule");
                return;
            }
            _scheduledAt = at;
            _sleepAfterSchedule = sleep.IsChecked == true;
            _scheduleTimer.Start();
            UpdateScheduleText();
            win.DialogResult = true;
        };
        win.ShowDialog();
    }

    private void UpdateScheduleText()
    {
        ScheduleText.Visibility = _scheduleTimer.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ScheduleText.Text = $"⏰ Scheduled {_scheduledAt:ddd d MMM HH:mm}" + (_sleepAfterSchedule ? " · then sleep" : "");
        if (_scheduleTimer.IsEnabled) StatusText.Text = $"Transcription scheduled for {_scheduledAt:dddd HH:mm}. Keep WhisperDesk open.";
    }

    private void OnScheduleTextClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OnSchedule(sender, e);
}
