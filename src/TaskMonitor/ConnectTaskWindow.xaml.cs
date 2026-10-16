using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TaskMonitor.Core;

namespace TaskMonitor;

public partial class ConnectTaskWindow : Window
{
    private readonly MonitorController _controller;
    private readonly int? _preferredPid;
    private List<ProcessDiscovery.ConnectableProcess> _processes = new();
    private bool _loading = true;
    private string? _loadedStatus;
    private const string None = "(연결 안 함)";
    public string? ConnectedId { get; private set; }

    public ConnectTaskWindow(MonitorController controller, int? preferredPid = null)
    {
        InitializeComponent();
        _controller = controller;
        _preferredPid = preferredPid;
        Loaded += async (_, _) => await RefreshProcesses();
    }

    private ProcessDiscovery.ConnectableProcess? Selected => ProcessList.SelectedItem as ProcessDiscovery.ConnectableProcess;
    private ComboBox[] Fields => new[] { StateField, StageField, DoneField, TotalField, SubjectField };
    private async Task RefreshProcesses()
    {
        SaveButton.IsEnabled = false;
        try
        {
            int? previous = Selected?.Pid ?? _preferredPid;
            _processes = await Task.Run(ProcessDiscovery.ConnectionProcesses);
            _loading = false;
            FilterProcesses(previous);
        }
        catch (Exception ex) { ErrorText.Text = "프로세스 목록을 읽지 못했습니다: " + ex.Message; }
    }

    private void FilterProcesses(int? selectedPid)
    {
        string search = SearchBox.Text.Trim();
        var items = _processes.Where(p => search.Length == 0 || p.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                                         p.CommandLine.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        ProcessList.ItemsSource = items;
        ProcessList.SelectedItem = items.FirstOrDefault(p => p.Pid == selectedPid);
        if (ProcessList.SelectedItem is not null) ProcessList.ScrollIntoView(ProcessList.SelectedItem);
        UpdatePreview();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshProcesses();
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading) FilterProcesses(Selected?.Pid);
    }

    private void Process_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } process) return;
        _loading = true;
        try
        {
            var doc = _controller.ConnectionDoc(process.Pid);
            var connection = doc?.Connection;
            string? log = connection?.LogFile ?? doc?.LogPath;
            var dirs = new List<string>();
            if (doc?.Results is { } results) dirs.AddRange(results.Where(r => Directory.Exists(r.Path)).Select(r => r.Path));
            if (!string.IsNullOrEmpty(log)) dirs.Add(Path.GetDirectoryName(log)!);
            if (!string.IsNullOrEmpty(doc?.Workdir)) dirs.Add(doc.Workdir);
            // 새 프로세스도 명령에 지정한 출력 폴더에서 후보를 찾는다.
            foreach (string argument in ProcessDiscovery.ConnectionArguments(process.CommandLine))
            {
                string value = argument.StartsWith('-') && argument.Contains('=') ? argument[(argument.IndexOf('=') + 1)..] : argument;
                if (Directory.Exists(value)) dirs.Add(value);
            }
            StatusBox.Text = connection?.StatusFile ?? WorkStatus.FindStatusFile(dirs.Distinct()) ?? "";
            LogBox.Text = log ?? dirs.SelectMany(d => new[] { Path.Combine(d, "worker.log"), Path.Combine(d, "run.log") }).FirstOrDefault(File.Exists) ?? "";
            LoadFields(connection);
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { _loading = false; }
        UpdatePreview();
    }

    private void LoadFields(TaskConnection? saved = null)
    {
        bool previous = _loading;
        _loading = true;
        try
        {
            var fields = string.IsNullOrWhiteSpace(StatusBox.Text) ? new Dictionary<string, System.Text.Json.JsonElement>() : ConnectionReader.ReadFields(StatusBox.Text.Trim());
            var suggestion = saved ?? (fields.Count > 0 ? ConnectionReader.Suggest(StatusBox.Text.Trim(), LogBox.Text.Trim()) : new TaskConnection());
            string?[] selections = { suggestion.StateField, suggestion.StageField, suggestion.DoneField, suggestion.TotalField, suggestion.SubjectField };
            for (int i = 0; i < Fields.Length; i++)
            {
                bool numeric = i is 2 or 3;
                Fields[i].ItemsSource = new[] { None }.Concat(fields.Where(p => numeric
                    ? p.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                    : p.Value.ValueKind == System.Text.Json.JsonValueKind.String).Select(p => p.Key)).ToList();
                Fields[i].SelectedItem = selections[i] is { } field && fields.ContainsKey(field) ? field : None;
            }
            UnitBox.Text = suggestion.Unit;
            TotalBox.Text = suggestion.Total?.ToString(CultureInfo.InvariantCulture) ?? "";
            _loadedStatus = StatusBox.Text.Trim();
        }
        finally { _loading = previous; }
    }

    private TaskConnection Configuration()
    {
        string? Field(ComboBox box) => box.SelectedItem is string field && field != None ? field : null;
        double? total = null;
        if (!string.IsNullOrWhiteSpace(TotalBox.Text))
        {
            if (!double.TryParse(TotalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number <= 0)
                throw new InvalidDataException("전체 수량은 0보다 큰 숫자를 입력하세요.");
            total = number;
        }
        return new TaskConnection
        {
            StatusFile = string.IsNullOrWhiteSpace(StatusBox.Text) ? null : Path.GetFullPath(StatusBox.Text.Trim()),
            LogFile = string.IsNullOrWhiteSpace(LogBox.Text) ? null : Path.GetFullPath(LogBox.Text.Trim()),
            StateField = Field(StateField), StageField = Field(StageField), DoneField = Field(DoneField), TotalField = Field(TotalField), SubjectField = Field(SubjectField),
            Total = total, Unit = string.IsNullOrWhiteSpace(UnitBox.Text) ? "items" : UnitBox.Text.Trim(),
        };
    }

    private void UpdatePreview()
    {
        if (_loading || PreviewText is null) return;
        SaveButton.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            var connection = Configuration();
            var lines = new List<string>();
            if (Selected is { } process) lines.Add(process.Label);
            else { PreviewText.Text = "연결할 프로세스를 선택하세요."; return; }
            if (connection.LogFile is { } log && !File.Exists(log)) throw new FileNotFoundException("로그 파일을 찾을 수 없습니다.");
            if (connection.StatusFile is not null)
            {
                var snapshot = ConnectionReader.Read(connection);
                string state = ConnectionReader.State(snapshot.State) switch
                {
                    "running" => "실행 중", "waiting" => "대기 중", "pending" => "대기", "validating" => "검증 중",
                    "succeeded" => "완료", "failed" => "실패", "cancelled" => "중단", _ => snapshot.State ?? "프로세스 상태 확인",
                };
                lines.Add("상태: " + state);
                if (snapshot.Stage is not null) lines.Add("단계: " + snapshot.Stage);
                if (snapshot.Subject is not null) lines.Add("입력: " + snapshot.Subject);
                if (snapshot.Done is double done) lines.Add($"처리량: {Fmt.Num(done)}" + (snapshot.Total is double count ? $" / {Fmt.Num(count)}" : " (전체 수량 미지정)") + " " + connection.Unit);
            }
            else lines.Add("프로세스의 실행 여부와 종료를 표시합니다.");
            if (connection.LogFile is not null)
            {
                var progress = LogProgressParser.ParseTail(TaskFiles.ReadTail(connection.LogFile, 30, out _));
                lines.Add(progress is null ? "로그 연결됨" : $"로그 진행률: {Fmt.Num(progress.Done)} / {(progress.Total is double total ? Fmt.Num(total) : "?")} {progress.Unit}");
            }
            PreviewText.Text = string.Join("\n", lines);
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; PreviewText.Text = "연결 정보를 확인하세요."; }
    }

    private void BrowseStatus_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "작업 상태 파일", Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        StatusBox.Text = dialog.FileName;
        ReloadFields();
    }
    private void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "작업 로그", Filter = "로그 (*.log;*.txt)|*.log;*.txt|모든 파일 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) LogBox.Text = dialog.FileName;
        UpdatePreview();
    }
    private void ReloadFields()
    {
        try { if (_loadedStatus != StatusBox.Text.Trim()) LoadFields(); UpdatePreview(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; SaveButton.IsEnabled = false; }
    }
    private void Status_Changed(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) { if (!_loading) ReloadFields(); }
    private void Log_Changed(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => UpdatePreview();
    private void Field_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void TextField_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void Preview_Click(object sender, RoutedEventArgs e) => UpdatePreview();
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (!SaveButton.IsEnabled || Selected is not { } process) return;
        try
        {
            ConnectedId = _controller.ConnectProcess(process, Configuration());
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
}
