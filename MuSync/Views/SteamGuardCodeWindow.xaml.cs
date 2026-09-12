using System.Windows;
using Wpf.Ui.Controls;
namespace MuSync;
/// <summary>Steam Guard 邮箱验证码输入框（替代原 WinForms InputBox）。</summary>
public partial class SteamGuardCodeWindow : FluentWindow
{
    /// <summary>确认后保存用户输入的验证码；取消为 null。</summary>
    public string? Code { get; private set; }

    internal SteamGuardCodeWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Code = CodeBox.Text?.Trim();
        DialogResult = true;
        Close();
    }

    /// <summary>模态弹出并返回验证码；用户取消返回 null。</summary>
    public static string? Prompt(Window owner, string prompt)
    {
        var window = new SteamGuardCodeWindow { Owner = owner };
        window.PromptText.Text = prompt;
        window.ShowDialog();
        return window.Code;
    }
}
