using AwsSsmPortForwarder.Core.Models;

namespace AwsSsmPortForwarder.App.Dialogs;

public sealed class RegionSelectionDialog : Form
{
    private readonly ComboBox _regions = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 280 };
    public string SelectedRegion => _regions.Text.Trim();

    public RegionSelectionDialog(string? current = null)
    {
        Text = "Select Region";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 140);
        Font = new Font("Segoe UI", 9f);

        _regions.Items.AddRange([
            "us-east-1", "us-east-2", "us-west-1", "us-west-2",
            "eu-west-1", "eu-west-2", "eu-central-1", "sa-east-1",
            "ap-southeast-1", "ap-northeast-1"
        ]);
        if (!string.IsNullOrWhiteSpace(current))
            _regions.Text = current;

        var tip = new Label
        {
            Text = "This profile does not define a Region. Choose one for this run.\nConsider saving region in the AWS profile.",
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            Location = new Point(16, 12)
        };
        _regions.Location = new Point(16, 60);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(160, 100), AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(240, 100), AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(SelectedRegion))
                DialogResult = DialogResult.None;
        };
        Controls.AddRange([tip, _regions, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

public sealed class TargetSelectionDialog : Form
{
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        Dock = DockStyle.Fill
    };

    public AwsTarget? Selected { get; private set; }

    public TargetSelectionDialog(IReadOnlyList<AwsTarget> targets)
    {
        Text = "Select bastion";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 320);
        Font = new Font("Segoe UI", 9f);

        _list.Columns.AddRange([
            new ColumnHeader { Text = "Instance ID", Width = 160 },
            new ColumnHeader { Text = "Name", Width = 140 },
            new ColumnHeader { Text = "Private IP", Width = 110 },
            new ColumnHeader { Text = "AZ", Width = 100 },
            new ColumnHeader { Text = "SSM", Width = 100 }
        ]);
        foreach (var t in targets)
        {
            _list.Items.Add(new ListViewItem([
                t.InstanceId, t.Name, t.PrivateIpAddress ?? "", t.AvailabilityZone, t.SsmPingStatus
            ]) { Tag = t });
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.Controls.Add(_list, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "Select", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count == 0) { DialogResult = DialogResult.None; return; }
            Selected = (AwsTarget)_list.SelectedItems[0].Tag!;
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        panel.Controls.Add(buttons, 0, 1);
        Controls.Add(panel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
