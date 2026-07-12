using System.ComponentModel;
using SnowRunnerWheelspinFinder;

namespace SnowRunnerUnified;

internal sealed class MainForm : Form
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly UnifiedService _service;
    private readonly System.Windows.Forms.Timer _displayTimer = new() { Interval = 100 };
    private readonly Label _gameState = ValueLabel();
    private readonly Label _motionState = ValueLabel();
    private readonly Label _smtState = ValueLabel();
    private readonly Label _liveMotion = ValueLabel();
    private readonly Label _liveControls = ValueLabel();
    private readonly Label _stallState = ValueLabel();
    private readonly TextBox _diagnostics = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), WordWrap = false };
    private readonly CheckBox _motionEnabled = new() { Text = "Send motion to FlyPT" };
    private readonly TextBox _motionHost = new();
    private readonly NumericUpDown _motionPort = Number(1, 65535, 4123);
    private readonly NumericUpDown _motionRate = Number(20, 120, 60);
    private readonly DataGridView _motionAxes = Grid();
    private readonly TextBox _device = new();
    private readonly DataGridView _bindings = Grid();
    private readonly ComboBox _clutchAxis = AxisBox();
    private readonly ComboBox _throttleAxis = AxisBox();
    private readonly NumericUpDown _clutchMin = Number(0, 65535, 0);
    private readonly NumericUpDown _clutchMax = Number(0, 65535, 65535);
    private readonly NumericUpDown _throttleMin = Number(0, 65535, 0);
    private readonly NumericUpDown _throttleMax = Number(0, 65535, 65535);
    private readonly CheckBox _clutchInvert = new() { Text = "Invert clutch" };
    private readonly CheckBox _throttleInvert = new() { Text = "Invert throttle" };
    private readonly NumericUpDown _biteStart = DecimalNumber(0, 1, .10m, .01m);
    private readonly NumericUpDown _biteEnd = DecimalNumber(0, 1, .95m, .01m);
    private readonly NumericUpDown _curve = DecimalNumber(.1m, 12, 6, .1m);
    private readonly NumericUpDown _idleThrottle = DecimalNumber(0, 1, .25m, .01m);
    private readonly CheckBox _stallEnabled = new() { Text = "Enable realistic stall" };
    private readonly NumericUpDown _wheelThreshold = DecimalNumber(0, 10, .35m, .05m);
    private readonly NumericUpDown _chassisThreshold = DecimalNumber(0, 5, .15m, .05m);
    private readonly NumericUpDown _stallThrottle = DecimalNumber(0, 1, .22m, .01m);
    private readonly NumericUpDown _stallClutch = DecimalNumber(0, 1, .75m, .01m);
    private readonly NumericUpDown _stallDelay = Number(50, 5000, 650);
    private readonly CheckBox _manual = new() { Text = "Manual gearbox" };
    private readonly CheckBox _analogClutch = new() { Text = "Analog clutch" };
    private readonly CheckBox _clutchShift = new() { Text = "Require clutch for shifts" };
    private readonly CheckBox _neutralStart = new() { Text = "Start in neutral" };

    public MainForm()
    {
        _settings = _store.Load();
        _service = new UnifiedService(_settings);
        Text = "SnowRunner Unified — Motion + Manual Gearbox";
        MinimumSize = new Size(940, 650);
        Size = new Size(1100, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Dashboard", BuildDashboard()));
        tabs.TabPages.Add(Page("Motion / FlyPT", BuildMotion()));
        tabs.TabPages.Add(Page("Controls & calibration", BuildControls()));
        tabs.TabPages.Add(Page("Clutch & stall", BuildClutchStall()));
        tabs.TabPages.Add(Page("Diagnostics", BuildDiagnostics()));

        var apply = new Button { Text = "Save & apply", AutoSize = true, Padding = new Padding(12, 5, 12, 5) };
        apply.Click += (_, _) => SaveFromControls();
        var reset = new Button { Text = "Restore safe defaults", AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
        reset.Click += (_, _) => LoadIntoControls(new AppSettings());
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), WrapContents = false };
        footer.Controls.Add(apply); footer.Controls.Add(reset);
        Controls.Add(tabs); Controls.Add(footer);

        LoadIntoControls(_settings);
        _displayTimer.Tick += (_, _) => RefreshLiveView();
        Shown += (_, _) => { _service.Start(); _displayTimer.Start(); };
        FormClosed += (_, _) => { _displayTimer.Stop(); _service.Dispose(); };
    }

    private Control BuildDashboard()
    {
        var panel = StackPanel();
        panel.Controls.Add(Heading("One app, three synchronized systems"));
        panel.Controls.Add(StatusCard("SnowRunner", _gameState));
        panel.Controls.Add(StatusCard("FlyPT motion", _motionState));
        panel.Controls.Add(StatusCard("Manual gearbox plug-in", _smtState));
        panel.Controls.Add(StatusCard("Live motion", _liveMotion));
        panel.Controls.Add(StatusCard("Pedals / gear", _liveControls));
        panel.Controls.Add(StatusCard("Stall logic", _stallState));
        var reconnect = new Button { Text = "Reconnect and rediscover", AutoSize = true, Margin = new Padding(10) };
        reconnect.Click += async (_, _) => { reconnect.Enabled = false; await _service.ReconnectAsync(); reconnect.Enabled = true; };
        panel.Controls.Add(reconnect);
        return panel;
    }

    private Control BuildMotion()
    {
        _motionAxes.Columns.Add(new DataGridViewTextBoxColumn { Name = "Axis", ReadOnly = true });
        _motionAxes.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled" });
        _motionAxes.Columns.Add("Gain", "Gain"); _motionAxes.Columns.Add("Offset", "Offset");
        _motionAxes.Columns.Add("Limit", "Limit");
        _motionAxes.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Invert" });
        _motionAxes.Columns.Add("Smoothing", "Smoothing 0..0.99");
        var layout = FormTable();
        AddRow(layout, "Output", _motionEnabled); AddRow(layout, "FlyPT host", _motionHost);
        AddRow(layout, "UDP port", _motionPort); AddRow(layout, "Update rate", _motionRate);
        layout.Controls.Add(_motionAxes, 0, layout.RowCount); layout.SetColumnSpan(_motionAxes, 2); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowCount++;
        return layout;
    }

    private Control BuildControls()
    {
        _bindings.Columns.Add(new DataGridViewTextBoxColumn { Name = "Action", ReadOnly = true });
        _bindings.Columns.Add("Binding", "Key / button");
        var layout = FormTable();
        AddRow(layout, "Controller", _device); AddRow(layout, "Clutch axis", _clutchAxis);
        AddRow(layout, "Clutch raw min", _clutchMin); AddRow(layout, "Clutch raw max", _clutchMax); AddRow(layout, "", _clutchInvert);
        AddRow(layout, "Throttle axis", _throttleAxis); AddRow(layout, "Throttle raw min", _throttleMin); AddRow(layout, "Throttle raw max", _throttleMax); AddRow(layout, "", _throttleInvert);
        layout.Controls.Add(new Label { Text = "Click a binding cell and press/type the desired key or controller button.", AutoSize = true, ForeColor = Color.DimGray }, 0, layout.RowCount);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, layout.RowCount)!, 2); layout.RowCount++;
        layout.Controls.Add(_bindings, 0, layout.RowCount); layout.SetColumnSpan(_bindings, 2); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowCount++;
        return layout;
    }

    private Control BuildClutchStall()
    {
        var layout = FormTable();
        AddRow(layout, "Gearbox", Flow(_manual, _analogClutch, _clutchShift, _neutralStart));
        AddRow(layout, "Clutch bite starts", _biteStart); AddRow(layout, "Clutch fully engaged", _biteEnd);
        AddRow(layout, "Engagement curve", _curve); AddRow(layout, "Idle take-off throttle", _idleThrottle);
        AddRow(layout, "Stall", _stallEnabled); AddRow(layout, "Wheel moving threshold (rad/s)", _wheelThreshold);
        AddRow(layout, "Chassis moving threshold (m/s)", _chassisThreshold); AddRow(layout, "Maximum throttle for stall", _stallThrottle);
        AddRow(layout, "Minimum clutch engagement", _stallClutch); AddRow(layout, "Stall delay (ms)", _stallDelay);
        return layout;
    }

    private Control BuildDiagnostics()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var proof = new Button { Text = "Restart wheel discovery", Dock = DockStyle.Top, Height = 35 };
        proof.Click += (_, _) => _ = _service.ReconnectAsync();
        panel.Controls.Add(_diagnostics); panel.Controls.Add(proof); return panel;
    }

    private void LoadIntoControls(AppSettings value)
    {
        _motionEnabled.Checked = value.Motion.Enabled; _motionHost.Text = value.Motion.Host;
        _motionPort.Value = Math.Clamp(value.Motion.Port, 1, 65535); _motionRate.Value = Math.Clamp(value.Motion.RateHz, 20, 120);
        _motionAxes.Rows.Clear();
        foreach (var name in new[] { "Pitch", "Roll", "Yaw", "Surge", "Sway", "Heave" })
        {
            var axis = value.Motion.Axes.TryGetValue(name, out var configured) ? configured : new AxisTuning();
            _motionAxes.Rows.Add(name, axis.Enabled, axis.Gain, axis.Offset, axis.Limit, axis.Invert, axis.Smoothing);
        }
        _device.Text = value.Controls.DeviceName; _clutchAxis.Text = value.Controls.ClutchAxis.Axis; _throttleAxis.Text = value.Controls.ThrottleAxis.Axis;
        SetValue(_clutchMin, value.Controls.ClutchAxis.Minimum); SetValue(_clutchMax, value.Controls.ClutchAxis.Maximum);
        SetValue(_throttleMin, value.Controls.ThrottleAxis.Minimum); SetValue(_throttleMax, value.Controls.ThrottleAxis.Maximum);
        _clutchInvert.Checked = value.Controls.ClutchAxis.Invert; _throttleInvert.Checked = value.Controls.ThrottleAxis.Invert;
        _bindings.Rows.Clear(); foreach (var item in value.Controls.Bindings) _bindings.Rows.Add(item.Key, item.Value);
        SetValue(_biteStart, value.Clutch.BiteStart); SetValue(_biteEnd, value.Clutch.BiteEnd); SetValue(_curve, value.Clutch.Curve); SetValue(_idleThrottle, value.Clutch.IdleThrottle);
        _stallEnabled.Checked = value.Stall.Enabled; SetValue(_wheelThreshold, value.Stall.WheelMovingRadPerSecond); SetValue(_chassisThreshold, value.Stall.ChassisMovingMetresPerSecond);
        SetValue(_stallThrottle, value.Stall.MaxThrottle); SetValue(_stallClutch, value.Stall.MinClutchEngagement); SetValue(_stallDelay, value.Stall.DelayMilliseconds);
        _manual.Checked = value.Smt.ManualGearbox; _analogClutch.Checked = value.Smt.AnalogClutch; _clutchShift.Checked = value.Smt.RequireClutchForShift; _neutralStart.Checked = value.Smt.StartInNeutral;
    }

    private void SaveFromControls()
    {
        _settings.Motion.Enabled = _motionEnabled.Checked; _settings.Motion.Host = _motionHost.Text.Trim(); _settings.Motion.Port = (int)_motionPort.Value; _settings.Motion.RateHz = (int)_motionRate.Value;
        _settings.Motion.Axes.Clear(); foreach (DataGridViewRow row in _motionAxes.Rows) if (!row.IsNewRow && row.Cells[0].Value is string name)
            _settings.Motion.Axes[name] = new AxisTuning { Enabled = Bool(row, 1), Gain = Double(row, 2, 1), Offset = Double(row, 3), Limit = Math.Abs(Double(row, 4, 100)), Invert = Bool(row, 5), Smoothing = Math.Clamp(Double(row, 6), 0, .99) };
        _settings.Controls.DeviceName = _device.Text.Trim(); ReadAxis(_settings.Controls.ClutchAxis, _clutchAxis, _clutchMin, _clutchMax, _clutchInvert); ReadAxis(_settings.Controls.ThrottleAxis, _throttleAxis, _throttleMin, _throttleMax, _throttleInvert);
        _settings.Controls.Bindings.Clear(); foreach (DataGridViewRow row in _bindings.Rows) if (!row.IsNewRow && row.Cells[0].Value is string action) _settings.Controls.Bindings[action] = Convert.ToString(row.Cells[1].Value) ?? "";
        _settings.Clutch.BiteStart = (double)_biteStart.Value; _settings.Clutch.BiteEnd = Math.Max((double)_biteEnd.Value, _settings.Clutch.BiteStart + .01); _settings.Clutch.Curve = (double)_curve.Value; _settings.Clutch.IdleThrottle = (double)_idleThrottle.Value;
        _settings.Stall.Enabled = _stallEnabled.Checked; _settings.Stall.WheelMovingRadPerSecond = (double)_wheelThreshold.Value; _settings.Stall.ChassisMovingMetresPerSecond = (double)_chassisThreshold.Value; _settings.Stall.MaxThrottle = (double)_stallThrottle.Value; _settings.Stall.MinClutchEngagement = (double)_stallClutch.Value; _settings.Stall.DelayMilliseconds = (int)_stallDelay.Value;
        _settings.Smt.ManualGearbox = _manual.Checked; _settings.Smt.AnalogClutch = _analogClutch.Checked; _settings.Smt.RequireClutchForShift = _clutchShift.Checked; _settings.Smt.StartInNeutral = _neutralStart.Checked;
        _store.Save(_settings); _service.ApplySettings(_settings); Text = "SnowRunner Unified — settings saved";
    }

    private void RefreshLiveView()
    {
        var state = _service.Latest; if (state is null) return;
        _gameState.Text = state.Telemetry.Attached ? $"Connected — {state.Wheelspin.Wheels.Count} wheels detected" : state.Telemetry.Status;
        _motionState.Text = state.MotionStatus; _smtState.Text = state.Smt.Status;
        var basis = state.Telemetry.RigidBody?.Basis; var accel = state.Telemetry.RigidBody?.LocalAcceleration?.Value;
        _liveMotion.Text = basis is null ? "Waiting" : $"Pitch {basis.PitchDeg:0.0}°  Roll {basis.RollDeg:0.0}°  Yaw {basis.YawDeg:0.0}° | Accel {(accel is null ? "—" : $"{accel.Value.X:0.00} / {accel.Value.Y:0.00} / {accel.Value.Z:0.00}")}";
        _liveControls.Text = state.Smt.Connected ? $"Gear {state.Smt.Gear}  Clutch {state.Smt.Clutch:P0}  Throttle {state.Smt.Throttle:P0}" : "Plug-in not connected";
        _stallState.Text = state.StallRequested ? "STALL requested" : "Running / no stall condition";
        _stallState.ForeColor = state.StallRequested ? Color.Firebrick : Color.DarkGreen;
        _diagnostics.Text = $"Telemetry: {state.Telemetry.Status}\r\nWheel reader: {state.Wheelspin.Status}\r\nWheel acquisition: {state.Wheelspin.AcquisitionHz:0.0} Hz\r\nPhysical wheels: {state.Wheelspin.Wheels.Count}\r\nChassis speed: {state.Wheelspin.ChassisSpeed:0.000} m/s\r\nSMT: {state.Smt.Status}\r\nMotion: {state.MotionStatus}\r\n\r\n" + string.Join("\r\n", state.Wheelspin.Logs.TakeLast(30));
    }

    private static TabPage Page(string title, Control child) { var page = new TabPage(title) { Padding = new Padding(8) }; child.Dock = DockStyle.Fill; page.Controls.Add(child); return page; }
    private static FlowLayoutPanel StackPanel() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14) };
    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 16), Margin = new Padding(8, 8, 8, 18) };
    private static Panel StatusCard(string title, Label value) { var p = new Panel { Width = 850, Height = 70, Margin = new Padding(8), BackColor = Color.FromArgb(245, 247, 250), Padding = new Padding(12) }; p.Controls.Add(value); p.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI Semibold", 10) }); value.Dock = DockStyle.Fill; return p; }
    private static Label ValueLabel() => new() { AutoEllipsis = true, Text = "Starting…", ForeColor = Color.FromArgb(45, 60, 75), Padding = new Padding(0, 5, 0, 0) };
    private static TableLayoutPanel FormTable() => new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), ColumnCount = 2, RowCount = 0, ColumnStyles = { new ColumnStyle(SizeType.Absolute, 270), new ColumnStyle(SizeType.Percent, 100) } };
    private static void AddRow(TableLayoutPanel t, string label, Control control) { var row = t.RowCount++; t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 9, 12, 7) }, 0, row); control.Anchor = AnchorStyles.Left | AnchorStyles.Right; control.Margin = new Padding(3, 5, 3, 5); t.Controls.Add(control, 1, row); }
    private static FlowLayoutPanel Flow(params Control[] items) { var p = new FlowLayoutPanel { AutoSize = true, WrapContents = true }; p.Controls.AddRange(items); return p; }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Color.White, MinimumSize = new Size(400, 180) };
    private static NumericUpDown Number(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value, ThousandsSeparator = true };
    private static NumericUpDown DecimalNumber(decimal min, decimal max, decimal value, decimal increment) => new() { Minimum = min, Maximum = max, Value = value, Increment = increment, DecimalPlaces = 2 };
    private static ComboBox AxisBox() { var b = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown }; b.Items.AddRange(["Axis 0", "Axis 1", "Axis 2", "Axis 3", "Axis 4", "Axis 5", "Axis 6", "Axis 7", "Slider X 0", "Slider Y 0", "Slider X 1", "Slider Y 1"]); return b; }
    private static void SetValue(NumericUpDown control, double value) => control.Value = Math.Clamp((decimal)value, control.Minimum, control.Maximum);
    private static void ReadAxis(AxisCalibration axis, ComboBox name, NumericUpDown min, NumericUpDown max, CheckBox invert) { axis.Axis = name.Text; axis.Minimum = (int)min.Value; axis.Maximum = (int)max.Value; axis.Invert = invert.Checked; }
    private static bool Bool(DataGridViewRow row, int column) => Convert.ToBoolean(row.Cells[column].Value ?? false);
    private static double Double(DataGridViewRow row, int column, double fallback = 0) => double.TryParse(Convert.ToString(row.Cells[column].Value), out var value) ? value : fallback;
}
