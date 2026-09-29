namespace YaxinMonitor.Windows;

internal sealed class LoginForm : Form
{
    private readonly LicenseClient _license;
    private readonly TextBox _username = new() { Width = 220 };
    private readonly TextBox _password = new() { Width = 220, UseSystemPasswordChar = true };
    private readonly Button _login = new() { Text = "登录", AutoSize = true };
    private readonly Button _cancel = new() { Text = "退出", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _message = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(300, 0) };

    public LoginForm(LicenseClient license)
    {
        _license = license;
        Text = "亚信全桌监控 · 授权登录";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont;
        AcceptButton = _login;
        CancelButton = _cancel;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(16), Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "请使用管理员分配的账号登录，登录后才能使用本工具。", AutoSize = true, Margin = new Padding(0, 0, 0, 12) }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        layout.Controls.Add(new Label { Text = "用户名", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_username, 1, 1);
        layout.Controls.Add(new Label { Text = "密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_password, 1, 2);
        layout.Controls.Add(_message, 0, 3);
        layout.SetColumnSpan(_message, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([_cancel, _login]);
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(new Label { Text = "本机编号：" + license.MachineId[..12], AutoSize = true, ForeColor = SystemColors.GrayText }, 0, 5);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 5)!, 2);
        Controls.Add(layout);

        _username.Text = license.LoadSavedUsername();
        _login.Click += async (_, _) => await LoginAsync();
        Shown += (_, _) => (_username.Text.Length > 0 ? _password : _username).Focus();
    }

    private async Task LoginAsync()
    {
        _login.Enabled = _username.Enabled = _password.Enabled = false;
        _message.ForeColor = SystemColors.GrayText;
        _message.Text = "正在向授权服务器验证...";
        try
        {
            LicenseLoginResult result = await _license.LoginAsync(_username.Text, _password.Text);
            if (result.Success)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _message.ForeColor = Color.Firebrick;
            _message.Text = result.Message;
            _password.SelectAll();
        }
        finally
        {
            if (!IsDisposed)
            {
                _login.Enabled = _username.Enabled = _password.Enabled = true;
                _password.Focus();
            }
        }
    }
}
