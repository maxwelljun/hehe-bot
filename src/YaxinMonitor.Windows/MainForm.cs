using System.Diagnostics;
using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal sealed class MainForm : Form
{
    private readonly StateStore _store;
    private readonly MonitorService _service;
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
    private readonly ComboBox _triggerSide = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly NumericUpDown _streakLength = NumberBox(20);
    private readonly ComboBox _direction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly TextBox _stakes = new() { Width = 150 };
    private readonly NumericUpDown _dailyLimit = NumberBox(1_000_000);
    private readonly NumericUpDown _reservedLimit = NumberBox(1_000_000);
    private readonly NumericUpDown _minimumSeconds = NumberBox(60);
    private readonly CheckBox _playSound = new() { Text = "下注及结算语音提醒", AutoSize = true };
    private readonly Button _start = new() { Text = "启动监控", AutoSize = true };
    private readonly Button _forceStart = new() { Text = "重置状态并启动", AutoSize = true };
    private readonly Button _stop = new() { Text = "停止监控", AutoSize = true, Enabled = false };
    private readonly Button _pause = new() { Text = "恢复自动下单", AutoSize = true, Enabled = false };
    private readonly Button _save = new() { Text = "保存设置", AutoSize = true };
    private readonly Button _reconcile = new() { Text = "订单对账", AutoSize = true };
    private readonly Label _connection = new() { AutoSize = true, Text = "未连接", Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _summary = new() { AutoSize = true, Text = "桌台 0 · 余额 0 · 今日 0 · 在途 0" };
    private readonly DataGridView _tables = new();
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly ToolTip _toolTips = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _trayOrders;
    private YaxinSettings _currentSettings;
    private bool _exiting;

    public MainForm(StateStore store, YaxinSettings settings)
    {
        _store = store;
        _currentSettings = settings;
        _service = new MonitorService(store, settings);
        _service.LogReceived += message => Ui(() => AddLog(message));
        _service.StatusChanged += message => Ui(() => _connection.Text = message);
        _service.SnapshotChanged += snapshot => Ui(() => ApplySnapshot(snapshot));

        Text = "亚信全桌监控";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1180, 760);
        Font = SystemFonts.MessageBoxFont;

        _mode.Items.AddRange(Enum.GetValues<MonitorMode>().Cast<object>().ToArray());
        _mode.Format += (_, args) => args.Value = args.ListItem switch
        {
            MonitorMode.ReadOnly => "只读",
            MonitorMode.Simulation => "模拟",
            MonitorMode.Live => "真实",
            _ => args.ListItem?.ToString()
        };
        _mode.SelectedItem = settings.Mode;
        _triggerSide.Items.AddRange(Enum.GetValues<StrategyTriggerSide>().Cast<object>().ToArray());
        _triggerSide.Format += (_, args) => args.Value = args.ListItem switch
        {
            StrategyTriggerSide.Both => "庄和闲",
            StrategyTriggerSide.BankerOnly => "仅庄",
            StrategyTriggerSide.PlayerOnly => "仅闲",
            _ => args.ListItem?.ToString()
        };
        _triggerSide.SelectedItem = settings.Strategy.TriggerSide;
        _streakLength.Minimum = 2;
        _streakLength.Value = settings.Strategy.StreakLength;
        _direction.Items.AddRange(Enum.GetValues<StrategyDirection>().Cast<object>().ToArray());
        _direction.Format += (_, args) => args.Value = args.ListItem switch
        {
            StrategyDirection.Opposite => "反向",
            StrategyDirection.Follow => "顺向",
            _ => args.ListItem?.ToString()
        };
        _direction.SelectedItem = settings.Strategy.Direction;
        _stakes.Text = string.Join(",", settings.Strategy.Stakes.Select(value => value.ToString("0")));
        _dailyLimit.Value = Clamp(settings.DailyStakeLimit, _dailyLimit.Maximum);
        _reservedLimit.Value = Clamp(settings.MaxReservedStake, _reservedLimit.Maximum);
        _minimumSeconds.Minimum = 1;
        _minimumSeconds.Value = Math.Clamp(settings.MinimumRemainingMilliseconds / 1000, 1, 60);
        _playSound.Checked = settings.PlayAcceptedSound;

        ConfigureGrid();
        Controls.Add(BuildLayout());

        _start.Click += StartClicked;
        _forceStart.Click += ForceStartClicked;
        _stop.Click += StopClicked;
        _pause.Click += PauseClicked;
        _save.Click += SaveClicked;
        _reconcile.Click += ReconcileClicked;
        FormClosing += OnFormClosing;

        ConfigureToolTips();

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("打开", null, (_, _) => ShowWindow());
        _trayOrders = new ToolStripMenuItem("恢复自动下单", null, (_, _) => ToggleOrdersFromTray());
        trayMenu.Items.Add(_trayOrders);
        trayMenu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "亚信全桌监控",
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        AddLog("当前策略：" + settings.Strategy.Summary + "。首次使用请启动监控并在 Chrome 中手动登录。");
        UpdateCommandState();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32));

        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4) };
        settings.Controls.AddRange([
            SectionLabel("设置"),
            LabelFor("模式"), _mode,
            LabelFor("触发走势"), _triggerSide,
            LabelFor("连续次数"), _streakLength,
            LabelFor("方向"), _direction,
            LabelFor("金额序列"), _stakes,
            LabelFor("每日上限"), _dailyLimit,
            LabelFor("在途上限"), _reservedLimit,
            LabelFor("安全余量(秒)"), _minimumSeconds,
            _playSound, _save
        ]);

        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 2, 0, 8) };
        commands.Controls.AddRange([
            SectionLabel("运行"), _start, _stop, _pause, _reconcile, _forceStart
        ]);

        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 4, 0, 8) };
        status.Controls.Add(_connection);
        status.Controls.Add(new Label { AutoSize = true, Text = "    " });
        status.Controls.Add(_summary);
        var dataButton = new Button { Text = "数据目录", AutoSize = true, Margin = new Padding(20, 0, 0, 0) };
        dataButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(_store.DirectoryPath);
            Process.Start(new ProcessStartInfo("explorer.exe", _store.DirectoryPath) { UseShellExecute = true });
        };
        status.Controls.Add(dataButton);

        var logGroup = new GroupBox { Text = "运行记录", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logGroup.Controls.Add(_log);
        root.Controls.Add(settings, 0, 0);
        root.Controls.Add(commands, 0, 1);
        root.Controls.Add(status, 0, 2);
        root.Controls.Add(_tables, 0, 3);
        root.Controls.Add(logGroup, 0, 4);
        return root;
    }

    private void ConfigureToolTips()
    {
        _toolTips.SetToolTip(_save, "保存当前设置，不启动监控。");
        _toolTips.SetToolTip(_start, "使用当前设置启动监控，并恢复上次运行状态。");
        _toolTips.SetToolTip(_stop, "停止监控服务。专用 Chrome 不会关闭。");
        _toolTips.SetToolTip(_pause, "只暂停或恢复新的自动订单；监控和已受理订单继续运行。");
        _toolTips.SetToolTip(_reconcile, "核对状态不明订单，并恢复对应的隔离桌台。");
        _toolTips.SetToolTip(_forceStart, "清空桌台、追注、订单和风险累计状态后启动。");
    }

    private void ConfigureGrid()
    {
        _tables.Dock = DockStyle.Fill;
        _tables.ReadOnly = true;
        _tables.AllowUserToAddRows = false;
        _tables.AllowUserToDeleteRows = false;
        _tables.AllowUserToOrderColumns = true;
        _tables.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _tables.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _tables.RowHeadersVisible = false;
        _tables.Columns.Add("id", "桌台 ID");
        _tables.Columns.Add("name", "桌台");
        _tables.Columns.Add("state", "状态");
        _tables.Columns.Add("shoe", "牌靴");
        _tables.Columns.Add("game", "局号");
        _tables.Columns.Add("run", "最新走势");
        _tables.Columns.Add("chase", "追注任务");
        _tables.Columns.Add("remaining", "剩余秒");
    }

    private async void StartClicked(object? sender, EventArgs args)
    {
        try
        {
            YaxinSettings settings = ReadSettings(requireLiveConfirmation: true);
            _service.UpdateSettings(settings);
            _store.SaveSettings(settings);
            _currentSettings = settings;
            StartService("正在启动专用 Chrome 和监控服务...");
            await Task.CompletedTask;
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void ForceStartClicked(object? sender, EventArgs args)
    {
        try
        {
            YaxinSettings settings = ReadSettings(requireLiveConfirmation: false);
            DialogResult result = MessageBox.Show(
                "重置状态并启动会清空上次保存的桌台、追注、未完成订单、当日累计金额和防重复状态，" +
                "然后按网页最新数据重新判断。设置、日志和订单审计不会删除。\n\n" +
                "如果网站仍有未完成订单，重置后可能造成重复下注。请先核对网站订单记录。\n\n" +
                $"模式：{ModeText(settings.Mode)}\n策略：{settings.Strategy.Summary}\n" +
                $"每日上限：{settings.DailyStakeLimit:0.##}\n在途上限：{settings.MaxReservedStake:0.##}\n\n确认重置状态并启动？",
                "确认重置状态并启动", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;

            _service.ResetRuntimeState(settings);
            _store.SaveSettings(settings);
            _currentSettings = settings;
            StartService("正在重置运行状态并启动专用 Chrome 和监控服务...");
            await Task.CompletedTask;
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void StartService(string message)
    {
        _service.Start(Path.Combine(_store.DirectoryPath, "chrome-profile"));
        SetSettingsEnabled(false);
        UpdateCommandState();
        AddLog(message);
    }

    private async void StopClicked(object? sender, EventArgs args)
    {
        _stop.Enabled = false;
        await _service.StopAsync();
        SetSettingsEnabled(true);
        UpdateCommandState();
    }

    private void PauseClicked(object? sender, EventArgs args)
    {
        try
        {
            if (_service.OrdersPaused) _service.ResumeOrders();
            else _service.PauseOrders();
            UpdateCommandState();
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void SaveClicked(object? sender, EventArgs args)
    {
        try
        {
            YaxinSettings settings = ReadSettings(requireLiveConfirmation: true);
            _service.UpdateSettings(settings);
            _store.SaveSettings(settings);
            _currentSettings = settings;
            AddLog("设置已保存。");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void ReconcileClicked(object? sender, EventArgs args)
    {
        try
        {
            IReadOnlyList<ReconciliationOrderView> orders = _service.GetReconciliationOrders();
            if (orders.Count == 0)
            {
                MessageBox.Show("没有需要人工对账的订单。", "订单对账",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            foreach (ReconciliationOrderView order in orders)
            {
                ManualOrderResolution? resolution = ShowReconciliationDialog(order, orders.Count);
                if (resolution is null) break;
                _service.ResolveOrder(order.OrderKey, resolution.Value);
            }

            int remaining = _service.GetReconciliationOrders().Count;
            if (remaining == 0)
                MessageBox.Show("待对账订单已全部处理。", "订单对账",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                AddLog($"仍有 {remaining} 笔订单待对账。");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private static ManualOrderResolution? ShowReconciliationDialog(ReconciliationOrderView order, int total)
    {
        using var dialog = new Form
        {
            Text = "订单对账",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(620, 285),
            Font = SystemFonts.MessageBoxFont
        };
        var text = new Label
        {
            AutoSize = false,
            Location = new Point(18, 16),
            Size = new Size(584, 190),
            Text = $"请先在网站订单记录中核对这笔订单，再选择处理结果。当前共有 {total} 笔待对账订单。\n\n" +
                $"状态：{(order.Status == "SettlementPending" ? "待外部结算" : "状态不明")}\n" +
                $"桌台：{order.TableId}\n方向：{SideText(order.Side)}\n金额：{order.Amount:0.##}\n档位：第 {order.Attempt} 档\n" +
                $"创建时间：{order.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n订单键：{order.OrderKey}\n\n" +
                "订单仍在等待结算或无法确认时，请选择“暂不处理”。"
        };
        var notPlaced = new Button
        {
            Text = "确认未下注", AutoSize = true, Location = new Point(250, 230),
            Enabled = order.Status == "Unknown"
        };
        var settled = new Button { Text = "确认已结算", AutoSize = true, Location = new Point(365, 230) };
        var cancel = new Button { Text = "暂不处理", AutoSize = true, Location = new Point(490, 230), DialogResult = DialogResult.Cancel };
        ManualOrderResolution? result = null;
        notPlaced.Click += (_, _) => { result = ManualOrderResolution.ConfirmedNotPlaced; dialog.DialogResult = DialogResult.OK; };
        settled.Click += (_, _) => { result = ManualOrderResolution.ConfirmedSettled; dialog.DialogResult = DialogResult.OK; };
        dialog.Controls.AddRange([text, notPlaced, settled, cancel]);
        dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK ? result : null;
    }

    private YaxinSettings ReadSettings(bool requireLiveConfirmation)
    {
        MonitorMode mode = _mode.SelectedItem is MonitorMode value ? value : MonitorMode.ReadOnly;
        StrategyTriggerSide triggerSide = _triggerSide.SelectedItem is StrategyTriggerSide trigger
            ? trigger : StrategyTriggerSide.Both;
        StrategyDirection direction = _direction.SelectedItem is StrategyDirection selectedDirection
            ? selectedDirection : StrategyDirection.Opposite;
        decimal[] stakes = _stakes.Text.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => decimal.TryParse(text, out decimal amount) ? amount
                : throw new ArgumentException($"无法识别金额：{text}"))
            .ToArray();
        var strategy = new StrategySettings
        {
            TriggerSide = triggerSide,
            StreakLength = checked((int)_streakLength.Value),
            Direction = direction,
            Stakes = stakes
        };
        var settings = _currentSettings with
        {
            Mode = mode,
            DailyStakeLimit = _dailyLimit.Value,
            MaxReservedStake = _reservedLimit.Value,
            MinimumRemainingMilliseconds = checked((int)_minimumSeconds.Value * 1000),
            PlayAcceptedSound = _playSound.Checked,
            Strategy = strategy
        };
        settings.Validate();
        if (mode == MonitorMode.Live && requireLiveConfirmation)
        {
            DialogResult result = MessageBox.Show(
                $"真实模式会按当前策略自动提交订单。\n\n策略：{strategy.Summary}\n每日上限：{settings.DailyStakeLimit:0.##}\n在途上限：{settings.MaxReservedStake:0.##}\n\n保存真实模式设置？",
                "确认真实模式", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) throw new OperationCanceledException("已取消真实模式设置。");
        }
        return settings;
    }

    private void ApplySnapshot(ServiceSnapshot snapshot)
    {
        _summary.Text = $"桌台 {snapshot.Tables.Count} · 余额 {snapshot.Balance:0.##} · 今日 {snapshot.DailyStake:0.##} · 在途 {snapshot.ReservedStake:0.##} · {snapshot.Bundle}";
        int firstDisplayedIndex = _tables.FirstDisplayedScrollingRowIndex;
        _tables.SuspendLayout();
        _tables.Rows.Clear();
        foreach (TableViewState row in snapshot.Tables)
            _tables.Rows.Add(row.TableId, row.Name, row.State, row.ShoeSeq, row.GameSeq, row.LatestRun,
                row.Chase, row.RemainingSeconds);
        if (firstDisplayedIndex >= 0 && _tables.Rows.Count > 0)
            _tables.FirstDisplayedScrollingRowIndex = Math.Min(firstDisplayedIndex, _tables.Rows.Count - 1);
        _tables.ResumeLayout();
        UpdateCommandState();
    }

    private void AddLog(string message)
    {
        _log.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        if (_log.Lines.Length > 1000)
            _log.Lines = _log.Lines[^800..];
    }

    private void UpdateCommandState()
    {
        bool running = _service.IsRunning;
        bool live = _currentSettings.Mode == MonitorMode.Live;
        string orderCommand = _service.OrdersPaused ? "恢复自动下单" : "暂停自动下单";
        _start.Enabled = !running;
        _stop.Enabled = running;
        _forceStart.Enabled = !running;
        _pause.Enabled = running && live;
        _pause.Text = orderCommand;
        _trayOrders.Enabled = running && live;
        _trayOrders.Text = orderCommand;
    }

    private void SetSettingsEnabled(bool enabled)
    {
        _mode.Enabled = enabled;
        _triggerSide.Enabled = enabled;
        _streakLength.Enabled = enabled;
        _direction.Enabled = enabled;
        _stakes.Enabled = enabled;
        _dailyLimit.Enabled = enabled;
        _reservedLimit.Enabled = enabled;
        _minimumSeconds.Enabled = enabled;
        _playSound.Enabled = enabled;
        _save.Enabled = enabled;
    }

    private void ToggleOrdersFromTray()
    {
        try
        {
            if (_service.OrdersPaused) _service.ResumeOrders();
            else _service.PauseOrders();
            Ui(UpdateCommandState);
        }
        catch (Exception exception) { Ui(() => ShowError(exception.Message)); }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        Hide();
        _tray.ShowBalloonTip(1500, "亚信全桌监控", "程序继续在系统托盘运行。", ToolTipIcon.Info);
    }

    private async Task ExitAsync()
    {
        _exiting = true;
        _tray.Visible = false;
        await _service.DisposeAsync();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTips.Dispose();
            _tray?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    private static Label LabelFor(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(10, 7, 2, 0) };
    private static Label SectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
        Margin = new Padding(0, 7, 8, 0)
    };
    private static NumericUpDown NumberBox(decimal maximum) => new() { Minimum = 0, Maximum = maximum, DecimalPlaces = 0, Width = 90, ThousandsSeparator = true };
    private static decimal Clamp(decimal value, decimal maximum) => Math.Min(Math.Max(value, 0), maximum);
    private static string SideText(BetSide side) => side == BetSide.Banker ? "庄" : "闲";
    private static string ModeText(MonitorMode mode) => mode switch
    {
        MonitorMode.ReadOnly => "只读",
        MonitorMode.Simulation => "模拟",
        MonitorMode.Live => "真实",
        _ => mode.ToString()
    };
    private static void ShowError(string message) => MessageBox.Show(message, "亚信全桌监控", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
