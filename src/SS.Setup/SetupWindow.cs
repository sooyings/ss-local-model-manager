using System;using System.IO;using System.Reflection;using System.Security.Cryptography;using System.Text;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using System.Windows.Media.Animation;using System.Windows.Media.Imaging;
namespace SSSetup {
 public sealed class SetupWindow:Window {
  readonly Options options;TextBlock phase,detail,percent,modelCount;TextBox folder;ProgressBar progress;Button install,open;CheckBox accepted;bool running,finished;RunResult result;
  static Brush B(string s){return (Brush)new BrushConverter().ConvertFromString(s);}
  static TextBlock T(string s,double size,string color="#F8F7F2",bool bold=false){return new TextBlock{Text=s,FontSize=size,Foreground=B(color),FontWeight=bold?FontWeights.Bold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,3)};}
  static Border Card(string color,UIElement child){return new Border{Background=B(color),CornerRadius=new CornerRadius(20),Padding=new Thickness(20),Child=child};}
  static void Put(Grid g,UIElement child,int col=0,int row=0){Grid.SetColumn(child,col);Grid.SetRow(child,row);g.Children.Add(child);}
  public SetupWindow(Options o){
   options=o;Title="SS本地模型管理器 · 安装";Width=780;Height=530;MinWidth=680;MinHeight=470;Background=B("#1D1D1F");FontFamily=new FontFamily("Microsoft YaHei UI");WindowStartupLocation=WindowStartupLocation.CenterScreen;
   var outer=new Grid{Margin=new Thickness(22)};outer.RowDefinitions.Add(new RowDefinition{Height=new GridLength(62)});outer.RowDefinitions.Add(new RowDefinition());outer.RowDefinitions.Add(new RowDefinition{Height=new GridLength(82)});outer.RowDefinitions.Add(new RowDefinition{Height=new GridLength(26)});Content=outer;
   var header=new DockPanel();var logo=Card("#B4EDF3",T("SS",20,"#1B1B1E",true));logo.Padding=new Thickness(11,5,11,5);logo.Width=58;logo.Height=46;logo.Margin=new Thickness(0,0,15,0);DockPanel.SetDock(logo,Dock.Left);header.Children.Add(logo);var names=new StackPanel();names.Children.Add(T("SS本地模型管理器",23,"#F8F7F2",true));names.Children.Add(T("LOCAL MODELS · 4.0.0-beta.1",10,"#B8B8BB"));header.Children.Add(names);Put(outer,header);
   var cards=new Grid();cards.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.25,GridUnitType.Star)});cards.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});cards.ColumnDefinitions.Add(new ColumnDefinition());cards.RowDefinitions.Add(new RowDefinition());cards.RowDefinitions.Add(new RowDefinition{Height=new GridLength(12)});cards.RowDefinitions.Add(new RowDefinition());Put(outer,cards,0,1);
   var left=new Grid();left.RowDefinitions.Add(new RowDefinition());left.RowDefinitions.Add(new RowDefinition{Height=new GridLength(64)});left.RowDefinitions.Add(new RowDefinition{Height=new GridLength(48)});var copy=new StackPanel();phase=T("准备安装",27,"#1B1B1E",true);copy.Children.Add(phase);detail=T("用户级安装 · 独立模型目录\n现有模型保留原位置。",12,"#372A28");copy.Children.Add(detail);Put(left,copy);
   var track=new StackPanel();percent=T("等待开始",12,"#1B1B1E",true);track.Children.Add(percent);progress=new ProgressBar{Minimum=0,Maximum=100,Height=9,Foreground=B("#1B1B1E"),Background=B("#E55D50"),BorderThickness=new Thickness(0)};track.Children.Add(progress);Put(left,track,0,1);
   install=new Button{Content="一键安装",Background=B("#1B1B1E"),Foreground=B("#FFFFFF"),FontSize=15,FontWeight=FontWeights.Bold,BorderThickness=new Thickness(0),Cursor=System.Windows.Input.Cursors.Hand};install.Click+=async(s,e)=>await BeginInstall();Put(left,install,0,2);var main=Card("#FF6C5C",left);Grid.SetRowSpan(main,3);Put(cards,main);
   var top=new StackPanel();top.Children.Add(T("安装与验证",18,"#1B1B1E",true));top.Children.Add(T("解压 → 文件校验 → 创建入口",12,"#302D4C"));top.Children.Add(T("进度来自真实安装事件",11,"#302D4C"));top.Children.Add(T("不自动更改显卡功率",11,"#302D4C"));Put(cards,Card("#9790FF",top),2,0);
   var device=new StackPanel();device.Children.Add(T("设备与模型",18,"#1B1B1E",true));device.Children.Add(T("x64 · "+Environment.ProcessorCount+" 逻辑线程",12,"#253A3D"));modelCount=T("安装后自动发现已有模型",12,"#253A3D");device.Children.Add(modelCount);device.Children.Add(T("不预装大模型或显示驱动",11,"#253A3D"));Put(cards,Card("#B4EDF3",device),2,2);
   var lower=new StackPanel{Margin=new Thickness(0,10,0,0)};lower.Children.Add(T("安装位置（应为空目录）",11,"#B8B8BB"));folder=new TextBox{Text=o.Target,FontSize=12,Padding=new Thickness(5),Background=B("#2B2B2D"),Foreground=B("#F8F7F2"),BorderBrush=B("#55555A")};lower.Children.Add(folder);accepted=new CheckBox{Content="我已阅读安装说明及第三方许可，允许创建程序入口。",Foreground=B("#B8B8BB"),Margin=new Thickness(0,7,0,0),FontSize=10};lower.Children.Add(accepted);Put(outer,lower,0,2);
   var foot=new DockPanel();foot.Children.Add(T("Beta测试版 · 模型运行能力以支持说明为准",9,"#B8B8BB"));open=new Button{Content="打开管理器",Visibility=Visibility.Collapsed,FontSize=11,Background=B("#CBD9CE"),Foreground=B("#1B1B1E"),Padding=new Thickness(8,1,8,1)};DockPanel.SetDock(open,Dock.Right);open.Click+=(s,e)=>{try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(options.Target,"SSLocalModelManager.exe")){UseShellExecute=true});Close();}catch(Exception ex){detail.Text=ex.Message;}};foot.Children.Add(open);Put(outer,foot,0,3);
   Closing+=(s,e)=>{if(running){e.Cancel=true;detail.Text="正在完成安装事务，请等待结果后关闭。";}};
  }
  async Task BeginInstall(){
   if(finished){Close();return;}if(running)return;if(accepted.IsChecked!=true){detail.Text="请先阅读并勾选安装说明。";return;}
   string target;try{target=Path.GetFullPath(folder.Text);if(!Path.IsPathRooted(folder.Text))throw new ArgumentException();}catch{detail.Text="请输入绝对安装路径。";return;}
   options.Target=target;options.AcceptedLicense=true;running=true;install.IsEnabled=folder.IsEnabled=accepted.IsEnabled=false;phase.Text="正在安装";detail.Text="部署程序与运行依赖，请稍候。";percent.Text="0%";
   try{result=await Task.Run(()=>{string core=SetupEntry.ExtractCore();return SetupRunner.Run(options,core,n=>Dispatcher.Invoke(()=>{progress.Value=n;percent.Text=n+"%";}));});
    phase.Text=result.ExitCode==0?"安装完成":"安装未完成";detail.Text=result.ExitCode==0?"管理器已安装。首次打开后扫描模型；模型运行尚需验证。":result.Error;open.Visibility=result.ExitCode==0?Visibility.Visible:Visibility.Collapsed;
   }catch(Exception ex){phase.Text="安装未完成";detail.Text=ex.Message;}finally{running=false;finished=true;install.Content="关闭";install.IsEnabled=true;}
  }
 }
 public static class SetupEntry {
  public static string ExtractCore(){
   string dir=Path.Combine(Path.GetTempPath(),"SS-Setup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);string path=Path.Combine(dir,"SS.CoreSetup.exe");
   using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("SS.CoreSetup.exe")){if(source==null)throw new InvalidDataException("安装内核缺失");using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))source.CopyTo(output);}
   string actual;using(var sha=SHA256.Create())using(var file=File.OpenRead(path))actual=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
   if(actual!=ReleasePayload.Sha256)throw new InvalidDataException("安装内核校验失败");return path;
  }
  [STAThread]public static int Main(string[] args){
   bool unattended=Array.IndexOf(args,"--unattended")>=0;
   try{if(args.Length==2&&args[0]=="--render"){
    var app=new Application();var w=new SetupWindow(new Options{Ok=true,Target="选择本机可写的程序目录"});var content=(UIElement)w.Content;w.Content=null;var root=new Border{Width=780,Height=530,Background=(Brush)new BrushConverter().ConvertFromString("#1D1D1F"),Child=content};root.Measure(new Size(780,530));root.Arrange(new Rect(0,0,780,530));root.UpdateLayout();var bmp=new RenderTargetBitmap(780,530,96,96,PixelFormats.Pbgra32);bmp.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using(var stream=File.Create(args[1]))png.Save(stream);return 0;
   }
   var options=Options.Parse(args);if(!options.Ok){if(!unattended)MessageBox.Show(options.Error,"SS安装");return 27;}
   if(options.Unattended)return SetupRunner.Run(options,ExtractCore(),null).ExitCode;
   return new Application().Run(new SetupWindow(options));
   }catch(Exception ex){if(!unattended)MessageBox.Show(ex.Message,"SS安装");return 26;}
  }
 }
}