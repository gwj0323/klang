using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klang_WinUI.Pages;

public sealed partial class NetEasePage : Page
{
    private bool _isLoggedIn = false;

    public NetEasePage()
    {
        InitializeComponent();
    }

    private void LoginBtn_Click(object sender, RoutedEventArgs e)
    {
        _isLoggedIn = !_isLoggedIn;
        if (_isLoggedIn)
        {
            LoginBtnText.Text = "退出账号";
            AccountStatusTitle.Text = "已连接：Hi-Res VIP 会员会话中";
        }
        else
        {
            LoginBtnText.Text = "扫码安全登录";
            AccountStatusTitle.Text = "尚未连接网易云音乐账号";
        }
    }
}
