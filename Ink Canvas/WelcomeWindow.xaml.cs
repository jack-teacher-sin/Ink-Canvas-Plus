using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace InkCanvasPlus
{
    /// <summary>
    /// WelcomeWindow.xaml 的交互逻辑
    /// </summary>
    public partial class WelcomeWindow : Window
    {
        public WelcomeWindow()
        {
            InitializeComponent();
            TextBlockVersion.Text = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            TextBlockUpdateContent.Inlines.Add(new Bold(new Run("Ink Canvas Plus 4.4.1 更新，祝您事事顺意！\n\n")));
            TextBlockUpdateContent.Inlines.Add(new Bold(new Run("【新功能】")));
            TextBlockUpdateContent.Inlines.Add(@"
· 展台抓拍批注：工具栏的展台图标会打开独立的展台窗口，抓拍画面直接贴到画板成为照片对象，
  可拖动、缩放、旋转、删除；照片左侧的裁剪按钮可以裁掉多余部分，只留要讲的那一块。
  墨迹始终画在照片之上，可以直接在实物画面上圈画讲解
· 黑板拖动：黑板模式左下角的十字箭头图标，在「书写」和「拖动」之间切换。
  进入拖动状态后，单指或笔在屏幕上拖动即可挪动整块黑板——板书墨迹、展台照片、
  直尺三角尺量角器都挂在同一个位移上一起走，相对位置不会乱。
  展台照片放大到超过屏幕时，可以这样把它平移到想看的区域

");
            TextBlockUpdateContent.Inlines.Add(new Bold(new Run("【改进】")));
            TextBlockUpdateContent.Inlines.Add(@"
· 工具栏的「尺规」「展台」图标由文字改为矢量绘制，缩放或更换主题都清晰
· 不再在启动时自动检查更新。以前查的是上游的更新源，它的下载地址指向上游安装包，
  上游版本号一旦超过本分支，点「更新」就会被换回上游版本。
  现在改为在设置里手动点「检查更新」，打开的是本仓库的下载页面
· 应用内「关于」和本窗口里的链接全部指向本仓库

");
            TextBlockUpdateContent.Inlines.Add(new Bold(new Run("【问题修复】")));
            TextBlockUpdateContent.Inlines.Add(@"
· 修复拖动黑板时，移动距离只有手指一半的问题
· 修复展台照片裁剪后，再拖动缩放手柄时照片位置跳动的问题

");
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        //本分支的更新日志就是本仓库的 Releases 页面：每个版本的更新说明都写在那里
        private void ButtonShowMore_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/jack-teacher-sin/Ink-Canvas-Plus/releases");
        }

        private void HyperlinkButtonWebsite_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/jack-teacher-sin/Ink-Canvas-Plus");
        }
    }
}
