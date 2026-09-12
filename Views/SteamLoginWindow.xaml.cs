using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
namespace MuSync;
/// <summary>
/// Steam 登录对话框：账号/密码/记住我，支持令牌自动登录与 Steam Guard 验证码。
/// 行为自 SteamLoginForm 逐行移植；Guard 回调来自后台线程，统一 Dispatcher marshal。
/// </summary>
public partial class SteamLoginWindow : FluentWindow
{
    private readonly SteamSessionManager _session;
    private bool _isLoginInProgress;
    public bool LoginSucceeded { get; private set; }

    internal SteamLoginWindow(SteamSessionManager session)
    {
        _session = session;
        InitializeComponent();
        var savedUsername = Configurations.Instance.Settings.SteamUsername;
        if (!string.IsNullOrEmpty(savedUsername))
        {
            UsernameBox.Text = savedUsername;
        }
        Loaded += async (_, _) => await AttemptAutoLogin();
    }

    private async Task AttemptAutoLogin()
    {
        var savedUser = Configurations.Instance.Settings.SteamUsername;
        var savedToken = Configurations.Instance.Settings.SteamRefreshToken;
        if (string.IsNullOrEmpty(savedUser) || string.IsNullOrEmpty(savedToken))
        {
            return;
        }
        _isLoginInProgress = true;
        UpdateUiState();
        SetStatus("正在自动登录...", info: true);
        var success = await Task.Run(() => _session.LoginWithTokenAsync(savedUser, savedToken));
        if (success)
        {
            LoginSucceeded = true;
            Close();
        }
        else
        {
            SetStatus("自动登录失败，请手动登录。", info: false);
            _isLoginInProgress = false;
            UpdateUiState();
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await PerformLogin();
    }

    private async Task PerformLogin()
    {
        if (_isLoginInProgress) return;
        var user = UsernameBox.Text.Trim();
        var pass = PasswordBox.Password.Trim();
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            SetStatus("请输入账号和密码", info: false);
            return;
        }
        _isLoginInProgress = true;
        UpdateUiState();
        SetStatus("正在登录...", info: true);
        // 勾选"记住我"才保存令牌用于下次自动登录
        _session.RememberSession = RememberCheckBox.IsChecked == true;
        _session.OnSteamGuardRequired += OnSteamGuardRequired;
        var success = await Task.Run(() => _session.LoginAsync(user, pass));
        _session.OnSteamGuardRequired -= OnSteamGuardRequired;
        if (success)
        {
            LoginSucceeded = true;
            Close();
        }
        else
        {
            SetStatus(_session.LoginError ?? "登录失败", info: false);
            _isLoginInProgress = false;
            UpdateUiState();
        }
    }

    private void OnSteamGuardRequired(bool isMobile)
    {
        // SteamGuardAuthenticator 回调发生在后台线程
        Dispatcher.Invoke(() =>
        {
            if (isMobile)
            {
                SetStatus("Steam Guard：请在手机 App 上确认登录！", info: true);
            }
            else
            {
                var code = SteamGuardCodeWindow.Prompt(this, "请输入邮箱验证码:");
                if (!string.IsNullOrEmpty(code))
                {
                    _session.SubmitSteamGuardCode(code);
                    SetStatus("正在验证...", info: true);
                }
                else
                {
                    _session.SubmitSteamGuardCode("");
                    SetStatus("已取消。", info: false);
                }
            }
        });
    }

    private void SetStatus(string message, bool info)
    {
        StatusText.Text = message;
        var brushKey = info ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush";
        if (TryFindResource(brushKey) is Brush brush) StatusText.Foreground = brush;
    }

    private void UpdateUiState()
    {
        UsernameBox.IsEnabled = !_isLoginInProgress;
        PasswordBox.IsEnabled = !_isLoginInProgress;
        RememberCheckBox.IsEnabled = !_isLoginInProgress;
        LoginButton.IsEnabled = !_isLoginInProgress;
        LoginButton.Content = _isLoginInProgress ? "..." : "登录";
        Cursor = _isLoginInProgress ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    /// <summary>未登录 Steam 的友好提示（原 Program.Main 启动流程的 MessageBox）。</summary>
    public static void ShowNotLoggedInHint()
    {
        MessageBox.Show(
            "未登录 Steam，音乐状态将不会同步到好友列表。\n\n" +
            "你可以在设置中重新登录。",
            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
