using System.Diagnostics;
using System.Drawing.Imaging;
using System.Media;
using Microsoft.Win32;
using ScreenWatch.Core;

namespace ScreenWatch.Windows;

internal sealed class MainForm : Form
{
    private readonly ProfileStore _store;
    private readonly ActivityLog _log;
    private MonitorSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Stopwatch _clock = new();
    private readonly List<Control> _editable = [];
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(35, 86, 161) };
    private readonly Label _regionLabel = new() { AutoSize = true };
    private readonly Label _score = new() { AutoSize = true, Text = "—", Font = new Font("Segoe UI", 26, FontStyle.Bold) };
    private readonly Label _detail = new() { AutoSize = true, Text = "先选择区域，再保存参考画面。" };
    private readonly PictureBox _referencePreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(237, 241, 247) };
    private readonly PictureBox _currentPreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(237, 241, 247) };
    private readonly TextBox _events = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly NumericUpDown _interval = Number(250, 60_000, 1000, 250);
    private readonly NumericUpDown _threshold = Number(50, 100, 95, 0.5m, 1);
    private readonly NumericUpDown _confirm = Number(1, 20, 2, 1);
    private readonly NumericUpDown _rearm = Number(1, 20, 2, 1);
    private readonly NumericUpDown _cooldown = Number(0, 3600, 30, 5);
    private readonly CheckBox _sound = new() { Text = "提示音", AutoSize = true, Checked = true };
    private readonly CheckBox _saveCapture = new() { Text = "命中时保存截图", AutoSize = true, Checked = true };
    private readonly Button _start = Button("开始监控");
    private readonly Button _stop = Button("停止监控");
    private readonly ToolStripMenuItem _trayStart = new("开始监控");
    private readonly ToolStripMenuItem _trayStop = new("停止监控");
    private byte[]? _referenceSample;
    private DetectionGate? _gate;
    private bool _running;
    private bool _tickBusy;
    private bool _operationBusy;
    private bool _exiting;
    private int _generation;
    private int _notificationCount;
    private string? _startupWarning;

    public MainForm(ProfileStore store, MonitorSettings settings)
    {
        _store = store;
        _settings = settings;
        _log = new ActivityLog(store.DirectoryPath);
        Text = "ScreenWatch · 画面监控";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(900, 800);
        MinimumSize = new Size(760, 650);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 247, 251);
        Icon = SystemIcons.Application;

        BuildLayout();
        _trayMenu.Items.Add("打开主窗口", null, (_, _) => RestoreWindow());
        _trayMenu.Items.Add(_trayStart);
        _trayMenu.Items.Add(_trayStop);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("打开数据目录", null, (_, _) => OpenDataDirectory());
        _trayMenu.Items.Add("退出程序", null, (_, _) => ExitApplication());
        _tray = new NotifyIcon { Icon = Icon, Text = "ScreenWatch · 已停止", ContextMenuStrip = _trayMenu, Visible = true };
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tray.BalloonTipClicked += (_, _) => RestoreWindow();
        _trayStart.Click += (_, _) => StartMonitoring();
        _trayStop.Click += (_, _) => StopMonitoring("已手动停止。");
        _start.Click += (_, _) => StartMonitoring();
        _stop.Click += (_, _) => StopMonitoring("已手动停止。");
        _timer.Tick += OnTimerTick;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        LoadProfile();
        SetStatus("已停止 · 配置完成后点击开始监控");
        UpdateControls();
        Shown += (_, _) =>
        {
            FitWindowToScreen();
            if (_startupWarning is not null) ReportError("参考图片加载失败，请重新保存参考画面。", new InvalidDataException(_startupWarning));
            try { _log.Prune(); }
            catch (Exception exception) { AddEvent($"清理旧记录失败：{exception.Message}"); }
        };
        FormClosing += OnClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 7, AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var heading = VerticalPanel();
        heading.Controls.Add(new Label { Text = "ScreenWatch", AutoSize = true, Font = new Font("Segoe UI", 23, FontStyle.Bold), ForeColor = Color.FromArgb(24, 43, 72) });
        heading.Controls.Add(new Label { Text = "固定区域画面匹配 · 本地提醒 · 托盘运行", AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
        heading.Controls.Add(_status);
        root.Controls.Add(heading, 0, 0);

        var regionPanel = VerticalPanel();
        regionPanel.Controls.Add(SectionLabel("1  选择画面"));
        regionPanel.Controls.Add(_regionLabel);
        var regionButtons = FlowPanel();
        var select = Button("框选区域");
        var capture = Button("保存当前画面为参考");
        var import = Button("导入参考图片");
        var test = Button("测试一次匹配");
        select.Click += async (_, _) => await SelectRegionAsync();
        capture.Click += async (_, _) => await CaptureReferenceAsync();
        import.Click += async (_, _) => await ImportReferenceAsync();
        test.Click += async (_, _) => await TestMatchAsync();
        regionButtons.Controls.AddRange([select, capture, import, test]);
        _editable.AddRange([select, capture, import, test]);
        regionPanel.Controls.Add(regionButtons);
        regionPanel.Controls.Add(new Label { Text = "框选和取样时主窗口会暂时隐藏。请让目标画面可见，区域尽量贴合目标。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 3, 0, 6) });
        root.Controls.Add(regionPanel, 0, 1);

        var rules = VerticalPanel();
        rules.Controls.Add(SectionLabel("2  匹配与提醒"));
        var fields = FlowPanel();
        fields.Controls.Add(Field("间隔（毫秒）", _interval));
        fields.Controls.Add(Field("阈值（%）", _threshold));
        fields.Controls.Add(Field("连续命中次数", _confirm));
        fields.Controls.Add(Field("消失确认次数", _rearm));
        fields.Controls.Add(Field("冷却（秒）", _cooldown));
        rules.Controls.Add(fields);
        var choices = FlowPanel();
        choices.Controls.AddRange([_sound, _saveCapture]);
        rules.Controls.Add(choices);
        _editable.AddRange([_interval, _threshold, _confirm, _rearm, _cooldown, _sound, _saveCapture]);
        root.Controls.Add(rules, 0, 2);

        var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 8, 0, 8) };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        previews.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previews.Controls.Add(new Label { Text = "参考画面", AutoSize = true }, 0, 0);
        previews.Controls.Add(new Label { Text = "最近取样", AutoSize = true }, 1, 0);
        previews.Controls.Add(_referencePreview, 0, 1);
        previews.Controls.Add(_currentPreview, 1, 1);
        root.Controls.Add(previews, 0, 3);

        var result = FlowPanel();
        result.Controls.Add(_score);
        _detail.Margin = new Padding(15, 18, 0, 8);
        result.Controls.Add(_detail);
        root.Controls.Add(result, 0, 4);
        _events.MinimumSize = new Size(0, 70);
        root.Controls.Add(_events, 0, 5);

        var footer = VerticalPanel();
        var actions = FlowPanel();
        _start.BackColor = Color.FromArgb(35, 99, 209);
        _start.ForeColor = Color.White;
        _start.FlatStyle = FlatStyle.Flat;
        var folder = Button("数据目录");
        folder.Click += (_, _) => OpenDataDirectory();
        var exit = Button("退出");
        exit.Click += (_, _) => ExitApplication();
        actions.Controls.AddRange([_start, _stop, folder, exit]);
        footer.Controls.Add(actions);
        footer.Controls.Add(new Label { Text = "开始后自动隐藏到托盘；关闭窗口也会隐藏。目标窗口需可见且无遮挡；锁屏或休眠后需重新开始。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 5, 0, 0) });
        root.Controls.Add(footer, 0, 6);
    }

    private void LoadProfile()
    {
        _interval.Value = _settings.IntervalMs;
        _threshold.Value = (decimal)_settings.Threshold;
        _confirm.Value = _settings.ConfirmFrames;
        _rearm.Value = _settings.RearmFrames;
        _cooldown.Value = _settings.CooldownSeconds;
        _sound.Checked = _settings.PlaySound;
        _saveCapture.Checked = _settings.SaveScreenshots;
        UpdateRegionLabel();
        if (_settings.ReferenceFile is { } file && _settings.Region is { } region)
        {
            try
            {
                using var bitmap = ScreenCapture.LoadReference(_store.GetReferencePath(file), region);
                _referenceSample = ScreenCapture.Sample(bitmap);
                SetPreview(_referencePreview, bitmap);
            }
            catch (Exception exception) { _startupWarning = exception.Message; }
        }
    }

    private MonitorSettings ReadSettings()
    {
        var settings = _settings with
        {
            IntervalMs = (int)_interval.Value,
            Threshold = (double)_threshold.Value,
            ConfirmFrames = (int)_confirm.Value,
            RearmFrames = (int)_rearm.Value,
            CooldownSeconds = (int)_cooldown.Value,
            PlaySound = _sound.Checked,
            SaveScreenshots = _saveCapture.Checked
        };
        settings.Validate();
        return settings;
    }

    private async Task SelectRegionAsync()
    {
        if (!BeginOperation()) return;
        try
        {
            Hide();
            await Task.Delay(350);
            if (_exiting) return;
            var region = await RegionSelector.SelectAsync();
            if (_exiting || region is null) return;
            var settings = ReadSettings() with { Region = region, ReferenceFile = null };
            _store.Save(settings);
            _settings = settings;
            _referenceSample = null;
            ClearPreview(_referencePreview);
            ClearPreview(_currentPreview);
            _score.Text = "—";
            _detail.Text = "区域已改变，请重新保存参考画面。";
            UpdateRegionLabel();
            CleanupReferences();
            AddEvent("已设置区域，请保存参考画面或导入相同尺寸的图片。");
        }
        catch (Exception exception) { ReportError("框选失败", exception); }
        finally { EndOperation(); }
    }

    private async Task CaptureReferenceAsync()
    {
        if (!BeginOperation()) return;
        try
        {
            var region = RequireRegion();
            using var bitmap = await CaptureWithoutWindowAsync(region);
            if (!_exiting) SaveReference(bitmap);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError("参考画面保存失败", exception); }
        finally { EndOperation(); }
    }

    private async Task ImportReferenceAsync()
    {
        if (!BeginOperation()) return;
        try
        {
            var region = RequireRegion();
            using var dialog = new OpenFileDialog { Title = $"选择 {region.Width} × {region.Height} 像素的参考图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            using var bitmap = await Task.Run(() => ScreenCapture.LoadReference(dialog.FileName, region));
            if (!_exiting) SaveReference(bitmap);
        }
        catch (Exception exception) { ReportError("导入失败", exception); }
        finally { EndOperation(); }
    }

    private async Task TestMatchAsync()
    {
        if (!BeginOperation()) return;
        try
        {
            var region = RequireRegion();
            var reference = _referenceSample ?? throw new InvalidOperationException("请先保存参考画面。");
            using var bitmap = await CaptureWithoutWindowAsync(region);
            if (_exiting) return;
            var result = ImageMatcher.Compare(reference, ScreenCapture.Sample(bitmap));
            UpdateMatchDisplay(result, bitmap, result.Score >= (double)_threshold.Value ? "测试：达到阈值" : "测试：未达到阈值");
            AddEvent($"手动测试：{result.Score:F2}%；当前阈值 {_threshold.Value:F1}%。测试不会发出命中提醒。");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError("测试失败", exception); }
        finally { EndOperation(); }
    }

    private async Task<Bitmap> CaptureWithoutWindowAsync(CaptureRegion region)
    {
        Hide();
        await Task.Delay(350);
        if (_exiting) throw new OperationCanceledException();
        return await Task.Run(() => ScreenCapture.Capture(region));
    }

    private void SaveReference(Bitmap bitmap)
    {
        byte[] sample = ScreenCapture.Sample(bitmap);
        string file = $"reference-{Guid.NewGuid():N}.png";
        string path = _store.GetReferencePath(file);
        var settings = ReadSettings() with { ReferenceFile = file };
        try
        {
            bitmap.Save(path, ImageFormat.Png);
            _store.Save(settings);
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw;
        }
        _settings = settings;
        _referenceSample = sample;
        SetPreview(_referencePreview, bitmap);
        ClearPreview(_currentPreview);
        _score.Text = "—";
        _detail.Text = "参考已保存，可先测试一次匹配。";
        CleanupReferences();
        AddEvent("参考画面已保存。");
    }

    private void CleanupReferences()
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(_store.DirectoryPath, "reference-*.png"))
                if (MonitorSettings.IsReferenceFileName(Path.GetFileName(path)) && Path.GetFileName(path) != _settings.ReferenceFile)
                    File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { AddEvent($"旧参考图清理失败：{exception.Message}"); }
    }

    private void StartMonitoring()
    {
        if (_running || _operationBusy || _tickBusy || _exiting) return;
        try
        {
            var region = RequireRegion();
            if (_referenceSample is null) throw new InvalidOperationException("请先保存参考画面。");
            if (!ScreenCapture.IsRegionAvailable(region)) throw new InvalidOperationException("原区域不可用，请重新框选。");
            ScreenCapture.EnsureInteractiveDesktop();
            var settings = ReadSettings();
            _store.Save(settings);
            _settings = settings;
            _gate = new DetectionGate(settings);
            _notificationCount = 0;
            _generation++;
            _running = true;
            _clock.Restart();
            _timer.Interval = settings.IntervalMs;
            SetStatus("监控中 · 等待画面匹配");
            _tray.Text = "ScreenWatch · 监控中";
            AddEvent($"开始监控：间隔 {settings.IntervalMs}ms，阈值 {settings.Threshold:F1}%，连续 {settings.ConfirmFrames} 次确认。");
            UpdateControls();
            Hide();
            _timer.Start();
        }
        catch (Exception exception) { ReportError("无法开始监控", exception); }
    }

    private void StopMonitoring(string reason, bool notify = false)
    {
        bool wasRunning = _running;
        _running = false;
        _generation++;
        _timer.Stop();
        _clock.Stop();
        SetStatus("已停止 · " + reason);
        _tray.Text = "ScreenWatch · 已停止";
        UpdateControls();
        if (wasRunning)
        {
            AddEvent(reason);
            if (notify && !_exiting) _tray.ShowBalloonTip(5000, "ScreenWatch 已停止", reason, ToolTipIcon.Warning);
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_running || _tickBusy || _settings.Region is not { } region || _referenceSample is not { } reference) return;
        _tickBusy = true;
        int generation = _generation;
        try
        {
            using var frame = await Task.Run(() => CaptureFrame(region, reference));
            if (_exiting || !_running || generation != _generation) return;
            var detection = _gate!.Observe(frame.Result.Score >= _settings.Threshold, _clock.Elapsed);
            string description = detection.State switch
            {
                DetectionState.Confirming => $"确认中 {detection.ConsecutiveMatches}/{_settings.ConfirmFrames}",
                DetectionState.CoolingDown => "已匹配 · 等待冷却结束",
                DetectionState.Latched => "本次出现已提醒 · 等待画面消失",
                _ => "等待画面匹配"
            };
            UpdateMatchDisplay(frame.Result, frame.Bitmap, description);
            SetStatus($"监控中 · {description} · 已提醒 {_notificationCount} 次");
            if (detection.ShouldNotify) NotifyMatch(frame);
        }
        catch (Exception exception)
        {
            if (!_exiting && generation == _generation)
                StopMonitoring($"截图或匹配失败：{exception.Message}", notify: true);
        }
        finally
        {
            _tickBusy = false;
            if (!_exiting) UpdateControls();
        }
    }

    private static CapturedFrame CaptureFrame(CaptureRegion region, byte[] reference)
    {
        var bitmap = ScreenCapture.Capture(region);
        try { return new(bitmap, ImageMatcher.Compare(reference, ScreenCapture.Sample(bitmap))); }
        catch { bitmap.Dispose(); throw; }
    }

    private void NotifyMatch(CapturedFrame frame)
    {
        _notificationCount++;
        string message = $"发现参考画面，相似度 {frame.Result.Score:F2}%，连续 {_settings.ConfirmFrames} 次确认。";
        AddEvent($"命中 #{_notificationCount}：{message}");
        _tray.ShowBalloonTip(5000, "ScreenWatch · 画面已出现", message, ToolTipIcon.Info);
        if (_settings.PlaySound) SystemSounds.Asterisk.Play();
        if (_settings.SaveScreenshots)
        {
            try { AddEvent("截图已保存：" + _log.SaveCapture(frame.Bitmap)); }
            catch (Exception exception) { AddEvent($"命中截图保存失败：{exception.Message}"); }
        }
        SetStatus($"监控中 · 本次出现已提醒 · 已提醒 {_notificationCount} 次");
    }

    private void UpdateMatchDisplay(MatchResult result, Bitmap bitmap, string description)
    {
        _score.Text = $"{result.Score:F1}%";
        _score.ForeColor = result.Score >= (double)_threshold.Value ? Color.FromArgb(21, 126, 89) : Color.FromArgb(35, 86, 161);
        _detail.Text = $"{description}\n差异像素 {result.ChangedPixelPercent:F1}% · {DateTime.Now:HH:mm:ss}";
        SetPreview(_currentPreview, bitmap);
    }

    private CaptureRegion RequireRegion() => _settings.Region ?? throw new InvalidOperationException("请先框选监控区域。");

    private bool BeginOperation()
    {
        if (_running || _operationBusy || _tickBusy || _exiting) return false;
        _operationBusy = true;
        UpdateControls();
        return true;
    }

    private void EndOperation()
    {
        _operationBusy = false;
        if (_exiting) return;
        RestoreWindow();
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool editable = !_running && !_operationBusy && !_tickBusy;
        foreach (var control in _editable) control.Enabled = editable;
        _start.Enabled = _trayStart.Enabled = editable && _settings.Region is not null && _referenceSample is not null;
        _stop.Enabled = _trayStop.Enabled = _running;
    }

    private void UpdateRegionLabel() => _regionLabel.Text = _settings.Region is { } r
        ? $"屏幕坐标 ({r.X}, {r.Y}) · {r.Width} × {r.Height} 像素"
        : "尚未选择区域";

    private void SetStatus(string text) => _status.Text = text;

    private void AddEvent(string message)
    {
        if (_exiting) return;
        if (_events.TextLength > 24_000) _events.Text = _events.Text[^12_000..];
        _events.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        try { _log.Write(message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { _events.AppendText($"日志写入失败：{exception.Message}{Environment.NewLine}"); }
    }

    private void ReportError(string title, Exception exception)
    {
        if (_exiting) return;
        RestoreWindow();
        AddEvent($"{title}：{exception.Message}");
        MessageBox.Show(this, exception.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RestoreWindow()
    {
        if (_exiting || IsDisposed) return;
        // Showing our own window may obscure the monitored area, so viewing pauses monitoring.
        if (_running) StopMonitoring("已打开主窗口；再次开始将隐藏窗口并恢复监控。");
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OpenDataDirectory()
    {
        if (_running) StopMonitoring("已打开数据目录，请检查目标画面后重新开始。");
        try { Process.Start(new ProcessStartInfo { FileName = _store.DirectoryPath, UseShellExecute = true }); }
        catch (Exception exception) { ReportError("无法打开目录", exception); }
    }

    private void PostStop(string reason)
    {
        if (_exiting || !IsHandleCreated || IsDisposed) return;
        try { BeginInvoke((Action)(() => { if (!_exiting) StopMonitoring(reason, notify: true); })); }
        catch (InvalidOperationException) { }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            PostStop("会话已锁定或断开，请回到桌面后手动开始。");
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume) PostStop("系统休眠或恢复，请检查目标画面后手动开始。");
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => PostStop("显示器配置已改变，请检查区域后手动开始。");

    private void ExitApplication()
    {
        if (_operationBusy)
        {
            MessageBox.Show("请先完成当前操作；框选时可按 Esc 取消。", "ScreenWatch");
            return;
        }
        try
        {
            _store.Save(ReadSettings());
        }
        catch (Exception exception)
        {
            RestoreWindow();
            if (MessageBox.Show(this, $"设置保存失败：{exception.Message}\n\n仍然退出吗？本次设置修改将不会保存。", "ScreenWatch",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        StopMonitoring("程序退出。");
        _exiting = true;
        Close();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing or CloseReason.ApplicationExitCall)
        {
            try { _store.Save(ReadSettings()); } catch (Exception) { }
            _exiting = true;
            return;
        }
        if (_exiting) return;
        e.Cancel = true;
        Hide();
        if (!_operationBusy)
        {
            try { _store.Save(ReadSettings()); }
            catch (Exception exception) { ReportError("设置保存失败", exception); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _generation++;
            _timer.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _tray.Visible = false;
            _tray.Dispose();
            _trayMenu.Dispose();
            ClearPreview(_referencePreview);
            ClearPreview(_currentPreview);
        }
        base.Dispose(disposing);
    }

    private static void SetPreview(PictureBox preview, Bitmap source)
    {
        // Keep previews small even when capturing a high-resolution region.
        double scale = Math.Min(1, 640.0 / Math.Max(source.Width, source.Height));
        var image = new Bitmap(source, new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))));
        var previous = preview.Image;
        preview.Image = image;
        previous?.Dispose();
    }

    private void FitWindowToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        int width = Math.Max(1, area.Width - 24);
        int height = Math.Max(1, area.Height - 24);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, width), Math.Min(MinimumSize.Height, height));
        Size = new Size(Math.Min(Width, width), Math.Min(Height, height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    private static void ClearPreview(PictureBox preview)
    {
        var previous = preview.Image;
        preview.Image = null;
        previous?.Dispose();
    }

    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, decimal increment, int decimals = 0) => new()
    { Minimum = minimum, Maximum = maximum, Value = value, Increment = increment, DecimalPlaces = decimals, Width = 125, ThousandsSeparator = true };

    private static Button Button(string text) => new()
    { Text = text, AutoSize = true, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(0, 4, 9, 4), Cursor = Cursors.Hand };

    private static Label SectionLabel(string text) => new()
    { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), Margin = new Padding(0, 15, 0, 6) };

    private static FlowLayoutPanel FlowPanel() => new()
    { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };

    private static FlowLayoutPanel VerticalPanel() => new()
    { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };

    private static Control Field(string label, Control input)
    {
        var field = VerticalPanel();
        field.Dock = DockStyle.None;
        field.Margin = new Padding(0, 0, 12, 5);
        field.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 5) });
        field.Controls.Add(input);
        return field;
    }

    private sealed record CapturedFrame(Bitmap Bitmap, MatchResult Result) : IDisposable
    { public void Dispose() => Bitmap.Dispose(); }
}
