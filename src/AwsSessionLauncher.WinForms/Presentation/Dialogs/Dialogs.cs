using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;

namespace AwsSessionLauncher.Presentation.Dialogs;

public sealed class PortForwardRuleDialog : Form
{
    private readonly TextBox _name = new() { Width = 280 };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly TextBox _host = new() { Width = 280 };
    private readonly NumericUpDown _remotePort = new() { Minimum = 1, Maximum = 65535, Value = 5432, Width = 280 };
    private readonly NumericUpDown _localPort = new() { Minimum = 1, Maximum = 65535, Value = 5432, Width = 280 };
    private readonly CheckBox _enabled = new() { Text = "Enabled", Checked = true, AutoSize = true };
    private readonly Guid _id;

    public PortForwardRule Result { get; private set; } = null!;

    public PortForwardRuleDialog(PortForwardRule? existing = null)
    {
        _id = existing?.Id ?? Guid.NewGuid();
        Text = existing is null ? "Add port forward" : "Edit port forward";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 300);
        Font = new Font("Segoe UI", 9f);

        _mode.Items.AddRange([nameof(PortForwardMode.RemoteHost), nameof(PortForwardMode.ManagedNode)]);
        _mode.SelectedIndex = 0;
        _mode.SelectedIndexChanged += (_, _) => UpdateHostEnabled();

        if (existing is not null)
        {
            _name.Text = existing.Name;
            _mode.SelectedItem = existing.Mode.ToString();
            _host.Text = existing.RemoteHost ?? "";
            _remotePort.Value = existing.RemotePort;
            _localPort.Value = existing.LocalPort;
            _enabled.Checked = existing.Enabled;
        }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(int row, string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        AddRow(0, "Name", _name);
        AddRow(1, "Mode", _mode);
        AddRow(2, "Remote host", _host);
        AddRow(3, "Remote port", _remotePort);
        AddRow(4, "Local port", _localPort);
        layout.Controls.Add(_enabled, 1, 5);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                MessageBox.Show(this, "Name is required.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            var mode = Enum.Parse<PortForwardMode>((string)_mode.SelectedItem!);
            Result = new PortForwardRule
            {
                Id = _id,
                Name = _name.Text.Trim(),
                Mode = mode,
                RemoteHost = mode == PortForwardMode.RemoteHost ? _host.Text.Trim() : null,
                RemotePort = (int)_remotePort.Value,
                LocalPort = (int)_localPort.Value,
                Enabled = _enabled.Checked
            };
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, 6);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        UpdateHostEnabled();
    }

    private void UpdateHostEnabled()
    {
        var remote = string.Equals(_mode.SelectedItem?.ToString(), nameof(PortForwardMode.RemoteHost), StringComparison.Ordinal);
        _host.Enabled = remote;
    }
}

public sealed class SetupPathsDialog : Form
{
    private readonly TextBox _config = new() { Width = 360 };
    private readonly TextBox _credentials = new() { Width = 360 };

    public string? ConfigPath => string.IsNullOrWhiteSpace(_config.Text) ? null : _config.Text.Trim();
    public string? CredentialsPath => string.IsNullOrWhiteSpace(_credentials.Text) ? null : _credentials.Text.Trim();

    public SetupPathsDialog(string? config, string? credentials)
    {
        Text = "AWS configuration paths";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 200);
        Font = new Font("Segoe UI", 9f);
        _config.Text = config ?? "";
        _credentials.Text = credentials ?? "";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        layout.Controls.Add(new Label { Text = "Config", AutoSize = true }, 0, 0);
        layout.Controls.Add(_config, 1, 0);
        var browseConfig = new Button { Text = "Browse", AutoSize = true };
        browseConfig.Click += (_, _) => Browse(_config, "AWS config|*");
        layout.Controls.Add(browseConfig, 2, 0);

        layout.Controls.Add(new Label { Text = "Credentials", AutoSize = true }, 0, 1);
        layout.Controls.Add(_credentials, 1, 1);
        var browseCreds = new Button { Text = "Browse", AutoSize = true };
        browseCreds.Click += (_, _) => Browse(_credentials, "AWS credentials|*");
        layout.Controls.Add(browseCreds, 2, 1);

        layout.Controls.Add(new Label
        {
            Text = "Tip: run 'aws configure sso' or 'aws configure' in a terminal if you have no profiles yet.",
            AutoSize = true,
            Dock = DockStyle.Fill
        }, 0, 2);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 2)!, 3);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, 3);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Browse(TextBox target, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            target.Text = dlg.FileName;
    }
}
