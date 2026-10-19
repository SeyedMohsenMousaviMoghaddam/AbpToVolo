using System.Collections.Concurrent;
using System.Diagnostics;
using AbpToVolo.Core;
using AbpToVolo.Steps;

namespace AbpToVolo.Desktop;

/// <summary>
/// Windows front-end of the converter: source/destination folders and options, a live list of the conversion steps
/// (status, duration, changes, manual items, warnings), progress and a colored log. English (default) or Persian UI.
/// Settings are remembered in %AppData%\AbpToVolo\settings.json and can be saved/loaded as files (same format as
/// "abp-to-volo --config"); the UI language in %AppData%\AbpToVolo\language.txt.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly string AppFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AbpToVolo");
    private static readonly string SettingsPath = Path.Combine(AppFolder, "settings.json");
    private static readonly string LanguagePath = Path.Combine(AppFolder, "language.txt");

    private readonly TextBox _source = PathBox();
    private readonly TextBox _destination = PathBox();
    private readonly TextBox _abpVersion = new() { Width = 90, RightToLeft = RightToLeft.No, TextAlign = HorizontalAlignment.Center };
    private readonly ComboBox _framework = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _migrations = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _idType = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _apiStyle = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _language = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _abpNames = Check();
    private readonly CheckBox _zero = Check();
    private readonly CheckBox _contracts = Check();
    private readonly CheckBox _tests = Check();
    private readonly CheckBox _host = Check();
    private readonly CheckBox _compilerFixes = Check();
    private readonly CheckBox _stubUnresolved = Check();
    private readonly CheckBox _build = Check();
    private readonly CheckBox _migrateDatabase = Check();
    private readonly CheckBox _verifyHost = Check();
    private readonly TextBox _connectionString = new() { Width = 260, RightToLeft = RightToLeft.No, UseSystemPasswordChar = true };
    private readonly CheckBox _force = Check();
    private readonly CheckBox _verbose = Check();

    private readonly Button _start = new() { AutoSize = true, Padding = new Padding(10, 4, 10, 4), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly Button _cancel = new() { AutoSize = true, Enabled = false, Padding = new Padding(6, 4, 6, 4) };
    private readonly Button _openDestination = new() { AutoSize = true, Enabled = false };
    private readonly Button _openReport = new() { AutoSize = true, Enabled = false };
    private readonly Button _openLog = new() { AutoSize = true, Enabled = false };
    private readonly Button _saveSettings = new() { AutoSize = true };
    private readonly Button _loadSettings = new() { AutoSize = true };

    // Texts that change with the language: control → key.
    private readonly Dictionary<Control, string> _texts = [];

    private readonly ListView _steps = new()
    {
        View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill, HideSelection = false, Font = new Font("Segoe UI", 9)
    };

    private readonly RichTextBox _log = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9.5f), RightToLeft = RightToLeft.No, WordWrap = false, BorderStyle = BorderStyle.None
    };

    private readonly ProgressBar _overallProgress = new() { Dock = DockStyle.Fill, Height = 18 };
    private readonly ProgressBar _stepProgress = new() { Dock = DockStyle.Fill, Height = 12 };
    private readonly Label _stepLabel = new() { AutoSize = true, RightToLeft = RightToLeft.No };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _elapsed = new() { Text = "" };

    private readonly ConcurrentQueue<(Color Color, string Text)> _pendingLog = new();
    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = 150 };
    private readonly Stopwatch _watch = new();

    private string _lang = UiText.English;
    private CancellationTokenSource? _cancellation;
    private string? _reportPath;
    private string? _logPath;

    public MainForm()
    {
        Font = new Font("Segoe UI", 9.5f);
        MinimumSize = new Size(1000, 720);
        Size = new Size(1280, 860);
        StartPosition = FormStartPosition.CenterScreen;

        _framework.Items.AddRange(["net10.0", "net9.0", "net8.0"]);
        _language.Items.AddRange(["English", "فارسی"]);
        foreach (var width in new[] { 40, 240, 110, 70, 70, 80, 70 }) _steps.Columns.Add("", width);

        Controls.Add(BuildLayout());
        _statusStrip.Items.AddRange([_status, _elapsed]);
        Controls.Add(_statusStrip);

        _start.Click += async (_, _) => await StartAsync();
        _cancel.Click += (_, _) => { _cancellation?.Cancel(); _cancel.Enabled = false; SetStatus(T("Stopping")); };
        _openDestination.Click += (_, _) => Open(_destination.Text);
        _openReport.Click += (_, _) => Open(_reportPath);
        _openLog.Click += (_, _) => Open(_logPath);
        _saveSettings.Click += (_, _) => SaveSettingsAs();
        _loadSettings.Click += (_, _) => LoadSettingsFrom();
        _language.SelectedIndexChanged += (_, _) =>
        {
            ApplyLanguage(_language.SelectedIndex == 1 ? UiText.Persian : UiText.English);
            TrySaveLanguage(_lang);
        };
        _flushTimer.Tick += (_, _) => FlushLog();
        _flushTimer.Start();
        FormClosing += (_, e) =>
        {
            if (_cancellation is not null && Ask(T("ConfirmClose"), MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cancellation?.Cancel();
            TrySave(ReadSettings(), SettingsPath);
        };

        ApplyLanguage(TryLoadLanguage());
        _language.SelectedIndex = _lang == UiText.Persian ? 1 : 0;
        ApplySettings(TryLoad(SettingsPath) ?? new MigrationSettings());
        SetStatus(T("Ready"));
        Log(Color.SkyBlue, "AbpToVolo — ASP.NET Boilerplate → ABP Framework (abp.io / Volo.Abp)");
        Log(Color.Gray, "Choose the source and destination folders, then press Start. The source folder is never modified.");
    }

    private string T(string key) => UiText.Get(_lang, key);

    private string T(string key, params object[] args) => string.Format(T(key), args);

    // ---------- language ----------

    /// <summary>Texts, combo items (the selection is kept) and the layout direction of the language.</summary>
    private void ApplyLanguage(string language)
    {
        _lang = language;
        var rtl = language == UiText.Persian;
        SuspendLayout();
        Text = T("Title");
        RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = rtl;
        _steps.RightToLeftLayout = rtl;
        _statusStrip.RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;

        foreach (var (control, key) in _texts) control.Text = T(key);

        SetItems(_migrations, "MigrationsSquash", "MigrationsDelete", "MigrationsKeep");
        SetItems(_idType, "IdSource", "IdGuid");
        SetItems(_apiStyle, "ApiBoilerplate", "ApiAbp");

        string[] columns = ["#", T("ColStep"), T("ColStatus"), T("ColTime"), T("ColChanges"), T("ColManual"), T("ColWarnings")];
        for (var i = 0; i < columns.Length; i++) _steps.Columns[i].Text = columns[i];
        if (_cancellation is null) SetStatus(T("Ready"));
        ResumeLayout(true);
    }

    private void SetItems(ComboBox combo, params string[] keys)
    {
        var selected = combo.SelectedIndex;
        combo.Items.Clear();
        combo.Items.AddRange(keys.Select(T).ToArray<object>());
        combo.SelectedIndex = selected >= 0 && selected < keys.Length ? selected : 0;
    }

    private TControl Localized<TControl>(TControl control, string key) where TControl : Control
    {
        _texts[control] = key;
        return control;
    }

    private static string TryLoadLanguage()
    {
        try
        {
            return File.Exists(LanguagePath) && File.ReadAllText(LanguagePath).Trim() == UiText.Persian ? UiText.Persian : UiText.English;
        }
        catch
        {
            return UiText.English;
        }
    }

    private static void TrySaveLanguage(string language)
    {
        try
        {
            Directory.CreateDirectory(AppFolder);
            File.WriteAllText(LanguagePath, language);
        }
        catch
        {
            // the language is only a preference
        }
    }

    // ---------- layout ----------

    private Control BuildLayout()
    {
        Localized(_abpNames, "AbpNames");
        Localized(_zero, "Zero");
        Localized(_contracts, "Contracts");
        Localized(_tests, "Tests");
        Localized(_host, "Host");
        Localized(_compilerFixes, "CompilerFixes");
        Localized(_stubUnresolved, "StubUnresolved");
        Localized(_build, "Build");
        Localized(_migrateDatabase, "MigrateDatabase");
        Localized(_verifyHost, "VerifyHost");
        Localized(_force, "Force");
        Localized(_verbose, "Verbose");
        Localized(_start, "Start");
        Localized(_cancel, "Cancel");
        Localized(_openDestination, "OpenDestination");
        Localized(_openReport, "OpenReport");
        Localized(_openLog, "OpenLog");
        Localized(_saveSettings, "SaveSettings");
        Localized(_loadSettings, "LoadSettings");

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // projects
        var projects = Localized(new GroupBox { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) }, "Projects");
        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(paths, 0, "Source", _source, "SourceDialog");
        AddPathRow(paths, 1, "Destination", _destination, "DestinationDialog");
        projects.Controls.Add(paths);
        root.Controls.Add(projects, 0, 0);

        // options
        var optionsBox = Localized(new GroupBox { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) }, "Options");
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        var checks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        checks.Controls.AddRange([_abpNames, _zero, _contracts, _tests, _host, _compilerFixes, _stubUnresolved, _build, _migrateDatabase, _verifyHost, _force, _verbose]);
        var values = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        AddValueRow(values, 0, "AbpVersion", _abpVersion);
        AddValueRow(values, 1, "Framework", _framework);
        AddValueRow(values, 2, "Migrations", _migrations);
        AddValueRow(values, 3, "IdType", _idType);
        AddValueRow(values, 4, "ApiStyle", _apiStyle);
        AddValueRow(values, 5, "ConnectionString", _connectionString);
        options.Controls.Add(checks, 0, 0);
        options.Controls.Add(values, 1, 0);
        optionsBox.Controls.Add(options);
        root.Controls.Add(optionsBox, 0, 1);

        // buttons (+ language)
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
        var languageLabel = Localized(new Label { AutoSize = true, Padding = new Padding(12, 8, 0, 0) }, "Language");
        buttons.Controls.AddRange([_start, _cancel, _openDestination, _openReport, _openLog, _saveSettings, _loadSettings, languageLabel, _language]);
        root.Controls.Add(buttons, 0, 2);

        // progress
        var progress = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3, AutoSize = true };
        progress.Controls.Add(_overallProgress, 0, 0);
        progress.Controls.Add(_stepProgress, 0, 1);
        progress.Controls.Add(_stepLabel, 0, 2);
        root.Controls.Add(progress, 0, 3);

        // steps + log
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, RightToLeft = RightToLeft.No };
        Shown += (_, _) => split.SplitterDistance = Math.Max(200, (int)(split.Width * 0.55));
        split.Resize += (_, _) => { if (split.Width > 400 && split.SplitterDistance > split.Width - 300) split.SplitterDistance = (int)(split.Width * 0.55); };
        var stepsBox = Localized(new GroupBox { Dock = DockStyle.Fill }, "Steps");
        stepsBox.Controls.Add(_steps);
        var logBox = Localized(new GroupBox { Dock = DockStyle.Fill }, "Log");
        logBox.Controls.Add(_log);
        split.Panel1.Controls.Add(logBox);
        split.Panel2.Controls.Add(stepsBox);
        root.Controls.Add(split, 0, 4);
        return root;
    }

    private static TextBox PathBox() => new() { Dock = DockStyle.Fill, RightToLeft = RightToLeft.No };

    private static CheckBox Check() => new() { AutoSize = true, Checked = true, Margin = new Padding(3, 2, 3, 2) };

    private void AddPathRow(TableLayoutPanel table, int row, string labelKey, TextBox box, string dialogKey)
    {
        var browse = Localized(new Button { AutoSize = true, MinimumSize = new Size(90, 28), Anchor = AnchorStyles.Left | AnchorStyles.Right }, "Browse");
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = T(dialogKey), UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
        };
        table.Controls.Add(Localized(new Label { AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) }, labelKey), 0, row);
        table.Controls.Add(box, 1, row);
        table.Controls.Add(browse, 2, row);
    }

    private void AddValueRow(TableLayoutPanel table, int row, string labelKey, Control control)
    {
        table.Controls.Add(Localized(new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, labelKey), 0, row);
        table.Controls.Add(control, 1, row);
    }

    // ---------- settings ----------

    private MigrationSettings ReadSettings() => new()
    {
        SourcePath = _source.Text.Trim(),
        DestinationPath = _destination.Text.Trim(),
        AbpVersion = string.IsNullOrWhiteSpace(_abpVersion.Text) ? "10.7.0" : _abpVersion.Text.Trim(),
        TargetFramework = _framework.SelectedItem as string ?? "net10.0",
        Migrations = _migrations.SelectedIndex switch { 1 => MigrationsMode.Delete, 2 => MigrationsMode.Keep, _ => MigrationsMode.Squash },
        IdType = _idType.SelectedIndex == 1 ? IdTypeMode.Guid : IdTypeMode.Source,
        ApiStyle = _apiStyle.SelectedIndex == 1 ? ApiStyleMode.Abp : ApiStyleMode.Boilerplate,
        UseAbpLayerNames = _abpNames.Checked,
        ConvertModuleZero = _zero.Checked,
        CreateContracts = _contracts.Checked,
        CreateTests = _tests.Checked,
        StandardizeHost = _host.Checked,
        CompilerFixes = _compilerFixes.Checked,
        StubUnresolved = _stubUnresolved.Checked,
        Build = _build.Checked,
        ConnectionString = string.IsNullOrWhiteSpace(_connectionString.Text) ? null : _connectionString.Text.Trim(),
        MigrateDatabase = _migrateDatabase.Checked,
        VerifyHost = _verifyHost.Checked,
        Force = _force.Checked,
        Verbose = _verbose.Checked
    };

    private void ApplySettings(MigrationSettings settings)
    {
        _source.Text = settings.SourcePath;
        _destination.Text = settings.DestinationPath;
        _abpVersion.Text = settings.AbpVersion;
        _framework.SelectedItem = _framework.Items.Contains(settings.TargetFramework) ? settings.TargetFramework : "net10.0";
        _migrations.SelectedIndex = settings.Migrations switch { MigrationsMode.Delete => 1, MigrationsMode.Keep => 2, _ => 0 };
        _idType.SelectedIndex = settings.IdType == IdTypeMode.Guid ? 1 : 0;
        _apiStyle.SelectedIndex = settings.ApiStyle == ApiStyleMode.Abp ? 1 : 0;
        _abpNames.Checked = settings.UseAbpLayerNames;
        _zero.Checked = settings.ConvertModuleZero;
        _contracts.Checked = settings.CreateContracts;
        _tests.Checked = settings.CreateTests;
        _host.Checked = settings.StandardizeHost;
        _compilerFixes.Checked = settings.CompilerFixes;
        _stubUnresolved.Checked = settings.StubUnresolved;
        _build.Checked = settings.Build;
        _migrateDatabase.Checked = settings.MigrateDatabase;
        _verifyHost.Checked = settings.VerifyHost;
        _force.Checked = settings.Force;
        _verbose.Checked = settings.Verbose;
    }

    private void SaveSettingsAs()
    {
        using var dialog = new SaveFileDialog { Filter = "Settings (*.json)|*.json", FileName = "abp-to-volo.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK && TrySave(ReadSettings(), dialog.FileName))
        {
            SetStatus(T("SettingsSaved", dialog.FileName));
        }
    }

    private void LoadSettingsFrom()
    {
        using var dialog = new OpenFileDialog { Filter = "Settings (*.json)|*.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK && TryLoad(dialog.FileName) is { } settings)
        {
            ApplySettings(settings);
            SetStatus(T("SettingsLoaded", dialog.FileName));
        }
    }

    private static MigrationSettings? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? MigrationSettings.Load(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySave(MigrationSettings settings, string path)
    {
        try
        {
            settings.Save(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- run ----------

    private async Task StartAsync()
    {
        var settings = ReadSettings();
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            ShowError(string.Join(Environment.NewLine, errors));
            return;
        }

        if (Directory.Exists(settings.DestinationPath) && Directory.EnumerateFileSystemEntries(settings.DestinationPath).Any() && !settings.Force)
        {
            if (Ask(T("ConfirmReplace"), MessageBoxIcon.Warning) != DialogResult.Yes) return;
            settings.Force = true;
        }

        TrySave(settings, SettingsPath);
        SetRunning(true);
        _log.Clear();
        _steps.Items.Clear();
        _reportPath = null;
        _logPath = null;
        _watch.Restart();

        var fileLog = new FileLogObserver();
        var observer = new CompositeObserver(new UiObserver(this, settings.Verbose), fileLog);
        _cancellation = new CancellationTokenSource();
        var options = settings.ToOptions();
        try
        {
            var report = await Task.Run(() => new MigrationEngine().Run(options, observer, _cancellation.Token));
            _reportPath = Path.Combine(options.OutputPath, ReportWriterStep.MarkdownFile);
            var todos = report.Entries.Count(e => e.Level is ReportLevel.Todo or ReportLevel.Warning);
            var build = report.BuildErrorCount is { } count ? (count == 0 ? T("BuildClean") : T("BuildErrors", count)) : "";
            SetStatus(T("Finished", report.CSharpFilesChanged, report.ProjectsMigrated, todos, build));
            MessageBox.Show(this, T("Succeeded", Environment.NewLine, _status.Text ?? "", _reportPath ?? ""), Text, MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, MessageOptions());
        }
        catch (OperationCanceledException)
        {
            SetStatus(T("StoppedByUser"));
        }
        catch (Exception ex)
        {
            Log(Color.IndianRed, ex.ToString());
            SetStatus(T("StoppedByError"));
            ShowError(ex.Message);
        }
        finally
        {
            _logPath = fileLog.LogFilePath;
            _cancellation.Dispose();
            _cancellation = null;
            _watch.Stop();
            SetRunning(false);
            FlushLog();
        }
    }

    private void SetRunning(bool running)
    {
        foreach (Control control in new Control[] { _start, _source, _destination, _abpVersion, _framework, _migrations, _idType, _apiStyle, _abpNames, _zero, _contracts, _tests, _host, _compilerFixes, _stubUnresolved, _build, _migrateDatabase, _verifyHost, _connectionString, _force, _verbose, _loadSettings, _language })
        {
            control.Enabled = !running;
        }

        _cancel.Enabled = running;
        _openDestination.Enabled = !running && Directory.Exists(_destination.Text);
        _openReport.Enabled = !running && File.Exists(_reportPath);
        _openLog.Enabled = !running && File.Exists(_logPath);
        UseWaitCursor = false;
    }

    private void Open(string? path)
    {
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private MessageBoxOptions MessageOptions() => _lang == UiText.Persian ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;

    private DialogResult Ask(string message, MessageBoxIcon icon) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2, MessageOptions());

    private void ShowError(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageOptions());

    private void SetStatus(string text) => _status.Text = text;

    // ---------- log ----------

    private void Log(Color color, string text) => _pendingLog.Enqueue((color, text));

    private void FlushLog()
    {
        if (_watch.IsRunning) _elapsed.Text = _watch.Elapsed.ToString(@"hh\:mm\:ss");
        if (_pendingLog.IsEmpty) return;

        _log.SuspendLayout();
        var count = 0;
        while (count++ < 2000 && _pendingLog.TryDequeue(out var line))
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = line.Color;
            _log.AppendText(line.Text + Environment.NewLine);
        }

        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
        _log.ResumeLayout();
    }

    // ---------- observer (called on the conversion thread) ----------

    private sealed class UiObserver(MainForm form, bool verbose) : IMigrationObserver
    {
        private DateTime _lastProgress = DateTime.MinValue;

        private void Ui(Action action)
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            form.BeginInvoke(action);
        }

        public void RunStarted(MigrationOptions options, IReadOnlyList<string> steps)
        {
            form.Log(Color.SkyBlue, $"Source:      {options.InputPath}");
            form.Log(Color.SkyBlue, $"Destination: {options.OutputPath}");
            form.Log(Color.Gray, $"ABP {options.AbpVersion} · {options.TargetFramework} · ABP.io names: {options.RenameLayers} · Module Zero → Volo modules: {options.ConvertModuleZero} · " +
                                 $"Contracts: {options.CreateContracts} · Tests: {options.CreateTests} · Host/DbMigrator: {options.StandardizeHost} · Migrations: {options.Migrations} · " +
                                 $"Compiler fixes: {options.CompilerFixes} · Stub unresolved: {options.StubUnresolved} · Build: {options.Build}");
            Ui(() =>
            {
                form._steps.BeginUpdate();
                for (var i = 0; i < steps.Count; i++)
                {
                    form._steps.Items.Add(new ListViewItem([(i + 1).ToString(), steps[i], form.T("Waiting"), "", "", "", ""]) { ForeColor = Color.Gray });
                }

                form._steps.EndUpdate();
                form._overallProgress.Maximum = steps.Count;
                form._overallProgress.Value = 0;
            });
        }

        public void StepStarted(StepInfo step)
        {
            form.Log(Color.White, $"{step.Position} {step.Name} ...");
            Ui(() =>
            {
                var item = form._steps.Items[step.Index];
                item.SubItems[2].Text = form.T("Running");
                item.ForeColor = Color.RoyalBlue;
                item.Font = new Font(form._steps.Font, FontStyle.Bold);
                item.EnsureVisible();
                form._stepProgress.Value = 0;
                form._stepLabel.Text = step.Name;
                form.SetStatus($"{step.Position} {step.Name}");
            });
        }

        public void StepProgress(StepInfo step, int current, int total, string? item)
        {
            if ((DateTime.UtcNow - _lastProgress).TotalMilliseconds < 80 && current < total) return;
            _lastProgress = DateTime.UtcNow;
            Ui(() =>
            {
                form._stepProgress.Maximum = Math.Max(1, total);
                form._stepProgress.Value = Math.Min(current, form._stepProgress.Maximum);
                form._stepLabel.Text = $"{step.Name}: {current}/{total} {item}";
            });
        }

        public void StepCompleted(StepInfo step, StepSummary summary)
        {
            form.Log(summary.Warnings > 0 ? Color.Goldenrod : Color.MediumSeaGreen,
                $"      ✓ {summary.Duration.TotalSeconds:0.0}s · {summary.Changes} changes · {summary.Todos} manual · {summary.Warnings} warnings");
            Ui(() =>
            {
                var item = form._steps.Items[step.Index];
                item.SubItems[2].Text = form.T(summary.Warnings > 0 ? "DoneWithWarnings" : "Done");
                item.SubItems[3].Text = $"{summary.Duration.TotalSeconds:0.0}s";
                item.SubItems[4].Text = summary.Changes.ToString();
                item.SubItems[5].Text = summary.Todos.ToString();
                item.SubItems[6].Text = summary.Warnings.ToString();
                item.ForeColor = summary.Warnings > 0 ? Color.DarkGoldenrod : Color.ForestGreen;
                item.Font = form._steps.Font;
                form._overallProgress.Value = Math.Min(step.Index + 1, form._overallProgress.Maximum);
                form._stepProgress.Value = form._stepProgress.Maximum;
            });
        }

        public void StepSkipped(StepInfo step, string reason)
        {
            form.Log(Color.DimGray, $"{step.Position} {step.Name} — skipped ({reason})");
            Ui(() =>
            {
                var item = form._steps.Items[step.Index];
                item.SubItems[2].Text = form.T("Skipped");
                item.ForeColor = Color.DarkGray;
                form._overallProgress.Value = Math.Min(step.Index + 1, form._overallProgress.Maximum);
            });
        }

        public void StepFailed(StepInfo step, Exception exception)
        {
            var cancelled = exception is OperationCanceledException;
            form.Log(Color.IndianRed, cancelled ? "      ✗ cancelled" : $"      ✗ {exception.Message}");
            Ui(() =>
            {
                var item = form._steps.Items[step.Index];
                item.SubItems[2].Text = form.T(cancelled ? "Cancelled" : "Failed");
                item.ForeColor = Color.Firebrick;
                item.Font = form._steps.Font;
            });
        }

        public void Log(LogLevel level, string message)
        {
            if (level == LogLevel.Detail && !verbose) return;
            form.Log(level switch
            {
                LogLevel.Warning => Color.Goldenrod,
                LogLevel.Error => Color.IndianRed,
                LogLevel.Success => Color.MediumSeaGreen,
                LogLevel.Detail => Color.DimGray,
                _ => Color.Silver
            }, "      " + message);
        }

        public void Entry(ReportEntry entry)
        {
            var location = entry.File is null ? "" : $"  ({entry.File}{(entry.Line is null ? "" : ":" + entry.Line)})";
            switch (entry.Level)
            {
                case ReportLevel.Warning:
                    form.Log(Color.Goldenrod, $"      ⚠ {entry.Message}{location}");
                    break;
                case ReportLevel.Todo when verbose:
                    form.Log(Color.Orange, $"      TODO {entry.Message}{location}");
                    break;
                case ReportLevel.Change when verbose:
                    form.Log(Color.DimGray, $"      · {entry.Message}{location}");
                    break;
            }
        }

        public void RunCompleted(MigrationReport report, TimeSpan duration, bool success)
        {
            form.Log(success ? Color.MediumSeaGreen : Color.IndianRed, success
                ? $"Done in {duration.TotalSeconds:0.0}s: {report.CSharpFilesChanged} C# files, {report.ProjectsMigrated} projects."
                : $"Stopped after {duration.TotalSeconds:0.0}s.");
        }
    }
}
