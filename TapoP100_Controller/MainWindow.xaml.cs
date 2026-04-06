using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TapoP100_Controller;

public partial class MainWindow : Window
{
    private readonly P100ApiServer _apiServer;

    public MainWindow()
    {
        InitializeComponent();
        _apiServer = new P100ApiServer(8080, HandleApiRequestAsync, AppendLog);

        Loaded += MainWindow_Loaded;
        IpAddressTextBox.TextChanged += (_, _) => SyncPreview();
        PlugOnButton.Click += async (_, _) => await ExecutePlugCommandAsync(
            turnOn: true,
            request: BuildRequestFromUi(),
            showMessageBox: true,
            source: "UI");
        PlugOffButton.Click += async (_, _) => await ExecutePlugCommandAsync(
            turnOn: false,
            request: BuildRequestFromUi(),
            showMessageBox: true,
            source: "UI");
        ClearLogButton.Click += (_, _) => LogTextBox.Clear();
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SyncPreview();
        ConnectionStatusTextBlock.Text = "입력 대기";
        LastCommandTextBlock.Text = "없음";
        AppendLog("Tapo P100 Controller ready");
        _apiServer.Start();
        AppendLog("HTTP API ready: POST http://<this-pc-ip>:8080/p100_on or /p100_off");
    }

    private void SyncPreview()
    {
        TargetIpPreviewTextBlock.Text = string.IsNullOrWhiteSpace(IpAddressTextBox.Text)
            ? "IP 미입력"
            : IpAddressTextBox.Text.Trim();
    }

    private ApiCommandRequest BuildRequestFromUi()
    {
        return new ApiCommandRequest(
            IpAddressTextBox.Text.Trim(),
            UsernameTextBox.Text.Trim(),
            PasswordInput.Password);
    }

    private async Task<ApiCommandResult> ExecutePlugCommandAsync(
        bool turnOn,
        ApiCommandRequest request,
        bool showMessageBox,
        string source)
    {
        if (string.IsNullOrWhiteSpace(request.TapoIp))
        {
            const string message = "tapo_ip 또는 Tapo IP 주소를 입력하세요.";
            if (showMessageBox)
            {
                MessageBox.Show(this, message, "입력 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return new ApiCommandResult(false, message);
        }

        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Pass))
        {
            const string message = "id 와 pass 값을 입력하세요.";
            if (showMessageBox)
            {
                MessageBox.Show(this, message, "입력 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return new ApiCommandResult(false, message);
        }

        SetControlsEnabled(false);
        ConnectionStatusTextBlock.Text = "연결 중";
        LastCommandTextBlock.Text = turnOn ? "플러그 ON 요청" : "플러그 OFF 요청";
        AppendLog($"Tapo {(turnOn ? "ON" : "OFF")} request: {request.TapoIp} / source={source}");

        if (string.Equals(source, "API", StringComparison.Ordinal))
        {
            IpAddressTextBox.Text = request.TapoIp;
            UsernameTextBox.Text = request.Id;
            PasswordInput.Password = request.Pass;
        }

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
            using TapoP100Controller controller = new(request.TapoIp, request.Id, request.Pass, AppendLog);

            if (turnOn)
            {
                await controller.TurnOnAsync(cts.Token);
                ConnectionStatusTextBlock.Text = "ON 완료";
                LastCommandTextBlock.Text = "플러그 ON 성공";
                return new ApiCommandResult(true, "플러그ON성공");
            }

            await controller.TurnOffAsync(cts.Token);
            ConnectionStatusTextBlock.Text = "OFF 완료";
            LastCommandTextBlock.Text = "플러그 OFF 성공";
            return new ApiCommandResult(true, "플러그OFF성공");
        }
        catch (Exception ex)
        {
            ConnectionStatusTextBlock.Text = "오류";
            LastCommandTextBlock.Text = "명령 실패";
            AppendLog($"Error: {ex.Message}");
            if (showMessageBox)
            {
                MessageBox.Show(this, ex.Message, "Tapo 제어 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return new ApiCommandResult(false, ex.Message);
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task<ApiCommandResult> HandleApiRequestAsync(ApiHttpRequest request)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return new ApiCommandResult(false, "POST만 지원합니다.");
        }

        if (request.Payload is null)
        {
            return new ApiCommandResult(false, "JSON 본문이 필요합니다.");
        }

        if (string.Equals(request.Path, "/p100_on", StringComparison.OrdinalIgnoreCase))
        {
            return await await Dispatcher.InvokeAsync(() => ExecutePlugCommandAsync(
                turnOn: true,
                request: request.Payload,
                showMessageBox: false,
                source: "API"));
        }

        if (string.Equals(request.Path, "/p100_off", StringComparison.OrdinalIgnoreCase))
        {
            return await await Dispatcher.InvokeAsync(() => ExecutePlugCommandAsync(
                turnOn: false,
                request: request.Payload,
                showMessageBox: false,
                source: "API"));
        }

        return new ApiCommandResult(false, "지원하지 않는 경로");
    }

    private void SetControlsEnabled(bool isEnabled)
    {
        IpAddressTextBox.IsEnabled = isEnabled;
        UsernameTextBox.IsEnabled = isEnabled;
        PasswordInput.IsEnabled = isEnabled;
        PlugOnButton.IsEnabled = isEnabled;
        PlugOffButton.IsEnabled = isEnabled;
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        if (string.IsNullOrWhiteSpace(LogTextBox.Text) || LogTextBox.Text == "[로그가 여기에 표시됩니다]")
        {
            LogTextBox.Text = line;
        }
        else
        {
            LogTextBox.AppendText(Environment.NewLine + line);
        }

        LogTextBox.ScrollToEnd();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _apiServer.Dispose();
    }
}
