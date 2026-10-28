using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using ModelLauncher;
using Sample=InferenceMonitor.Snapshot;
namespace ModelConsole {
 public class ConsoleWindow:Window {
  public Preferences Prefs;
  public Grid Surface {get;private set;}
  public int CurrentPage {get;private set;}
  readonly InferenceMonitor.Collector collector=new InferenceMonitor.Collector();
  readonly HostSample host=new HostSample();readonly NvidiaThermals thermals=new NvidiaThermals();
  readonly TelemetryHistory history=new TelemetryHistory();
  readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
  List<ModelEntry> models;string selected="",noticeZh="",noticeEn="";string notice{get{return L(noticeZh,noticeEn);}set{noticeZh=value;noticeEn=value;}} void SetNotice(string zh,string en){noticeZh=zh;noticeEn=en;}bool polling,closed,busy,paused;int generation;int modelPage;
  Sample last=new Sample();ModelLauncher.Snapshot service=new ModelLauncher.Snapshot();
  DateTime updated,registryStamp;bool freshHistory=true;
  Dictionary<string,TextBlock> texts=new Dictionary<string,TextBlock>();
  Dictionary<string,Meter> meters=new Dictionary<string,Meter>();
  Dictionary<string,Gauge> gauges=new Dictionary<string,Gauge>();
  List<Button> operationButtons=new List<Button>();
  List<Border> cards=new List<Border>();List<Button> modelButtons=new List<Button>();Dictionary<Button,ModelEntry> homeChoices=new Dictionary<Button,ModelEntry>();
  Point? titleDown;Plot trend;TextBlock footer;Button pauseButton,engineButton;bool sampleError;
  string Bg{get{return Prefs.Light?"#F1F0EB":"#1D1D1F";}}
  string Panel{get{return Prefs.Light?"#FFFFFF":"#2B2B2D";}}
  string Fore{get{return Prefs.Light?"#202021":"#F8F7F2";}}
  string Muted{get{return Prefs.Light?"#5F6165":"#B8B8BB";}}
  string Coral{get{return "#FF6C5C";}}
  string Violet{get{return Prefs.Light?"#DDD8FF":"#9790FF";}}
  string Cyan{get{return Prefs.Light?"#C3F4F8":"#B4EDF3";}}
  string Sage{get{return Prefs.Light?"#DAE5DC":"#CBD9CE";}}
  const string Dark="#1B1B1E";
  public string L(string zh,string en){return Prefs.Language=="en"?en:zh;}
  public ConsoleWindow(){
   Prefs=Preferences.Load();models=Registry.Load();registryStamp=File.GetLastWriteTimeUtc(Registry.ModelsJson);selected=Registry.ActiveId();
   if(!models.Any(x=>x.Id==selected))selected=models.Count>0?models[0].Id:"";
   Width=Math.Max(576,SystemParameters.PrimaryScreenWidth/4);Height=Math.Max(324,SystemParameters.PrimaryScreenHeight/4);
   MinWidth=576;MinHeight=324;MaxHeight=SystemParameters.WorkArea.Height;MaxWidth=SystemParameters.WorkArea.Width;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;
   WindowStartupLocation=WindowStartupLocation.CenterScreen;
   WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=0,ResizeBorderThickness=new Thickness(5),CornerRadius=new CornerRadius(16),GlassFrameThickness=new Thickness(0)});
   modelPage=Math.Max(0,models.FindIndex(x=>x.Id==selected))/4;
   Topmost=Prefs.Topmost;Build();
   timer.Tick+=async(s,e)=>{if(WindowState!=WindowState.Minimized)await Poll();};
   Loaded+=async(s,e)=>{timer.Start();AnimateCards();await Poll();if(models.Count==0&&Array.IndexOf(Environment.GetCommandLineArgs(),"--preview")<0)await ScanModels();};
   Closed+=(s,e)=>{closed=true;timer.Stop();};
   StateChanged+=async(s,e)=>{if(WindowState!=WindowState.Minimized&&!closed)await Poll();};
   PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Space&&!(Keyboard.FocusedElement is TextBox)&&!(Keyboard.FocusedElement is Button)){TogglePause();e.Handled=true;}};
  }
  static bool InteractiveTitleSource(DependencyObject node){
   while(node!=null){if(node is ButtonBase||node is TextBoxBase||node is Slider||node is ComboBox)return true;
    node=node is Visual||node is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);}
   return false;
  }
  void TitleDown(object sender,MouseButtonEventArgs e){
   if(e.ChangedButton!=MouseButton.Left||InteractiveTitleSource(e.OriginalSource as DependencyObject))return;
   if(e.ClickCount==2){titleDown=null;WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;e.Handled=true;return;}
   if(WindowState==WindowState.Maximized){titleDown=e.GetPosition(this);((UIElement)sender).CaptureMouse();e.Handled=true;return;}
   try{DragMove();}catch(InvalidOperationException ex){LogError(ex);}e.Handled=true;
  }
  void TitleMove(object sender,MouseEventArgs e){
   if(!titleDown.HasValue||e.LeftButton!=MouseButtonState.Pressed)return;
   Point client=e.GetPosition(this);Vector delta=client-titleDown.Value;
   if(Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance)return;
   var source=PresentationSource.FromVisual(this);if(source==null||source.CompositionTarget==null)return;
   Point screen=source.CompositionTarget.TransformFromDevice.Transform(PointToScreen(client));
   titleDown=null;Mouse.Capture(null);RestoreForTitleDrag(screen,client);
   try{DragMove();}catch(InvalidOperationException ex){LogError(ex);}e.Handled=true;
  }
  void TitleUp(object sender,MouseButtonEventArgs e){titleDown=null;if(Mouse.Captured==sender)Mouse.Capture(null);}
  public void RestoreForTitleDrag(Point screen,Point client){
   if(WindowState!=WindowState.Maximized)return;
   Rect saved=RestoreBounds;double ratio=Math.Max(0,Math.Min(1,client.X/Math.Max(1,ActualWidth)));
   double y=client.Y/Math.Max(1,ActualHeight)*saved.Height;
   WindowState=WindowState.Normal;Left=screen.X-saved.Width*ratio;Top=screen.Y-Math.Max(0,Math.Min(saved.Height*.12,y));
  }
  static Grid GridOf(double[] cols,double[] rows){var g=new Grid();foreach(var c in cols)g.ColumnDefinitions.Add(new ColumnDefinition{Width=c==0?new GridLength(1,GridUnitType.Star):new GridLength(c)});foreach(var r in rows)g.RowDefinitions.Add(new RowDefinition{Height=r==0?new GridLength(1,GridUnitType.Star):new GridLength(r)});return g;}
  static void Put(Grid g,UIElement e,int c=0,int r=0){Grid.SetColumn(e,c);Grid.SetRow(e,r);g.Children.Add(e);}
  TextBlock T(string text,double size=12,string color=null,bool bold=false){
   return new TextBlock{Text=text,FontSize=size,Foreground=Ink.B(color??Fore),FontWeight=bold?FontWeights.Bold:FontWeights.Normal,FontFamily=new FontFamily("Microsoft YaHei UI"),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
  }
  TextBlock Value(string key,string initial="—",double size=12,string color=null,bool bold=false){var t=T(initial,size,color,bold);texts[key]=t;return t;}
  void Text(string key,string value){TextBlock t;if(texts.TryGetValue(key,out t)){t.Text=value;t.ToolTip=value;}}
  Button B(string label,Action action,string id=null,string background=null,string foreground=null){
   var b=new Button{Content=label,FontSize=11,FontWeight=FontWeights.SemiBold,Background=Ink.B(background??Panel),Foreground=Ink.B(foreground??Fore),BorderBrush=Ink.B(Fore),Cursor=Cursors.Hand,Padding=new Thickness(6,3,6,3),RenderTransformOrigin=new Point(.5,.5),RenderTransform=new ScaleTransform(1,1)};
   if(id!=null){AutomationProperties.SetAutomationId(b,id);AutomationProperties.SetName(b,label);}
   b.Click+=(s,e)=>action();
   b.MouseEnter+=(s,e)=>Scale(b,1.035);b.MouseLeave+=(s,e)=>Scale(b,1);
   return b;
  }
  void Scale(Button b,double to){if(Prefs.ReducedMotion)return;var tr=(ScaleTransform)b.RenderTransform;var a=new DoubleAnimation(to,TimeSpan.FromMilliseconds(170)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};tr.BeginAnimation(ScaleTransform.ScaleXProperty,a);tr.BeginAnimation(ScaleTransform.ScaleYProperty,a);}
  Button Small(string label,string hint,Action action,string id){var b=B(label,action,id);b.ToolTip=hint;b.Width=26;b.Height=25;b.Margin=new Thickness(2,0,0,0);return b;}
  Border Card(Grid parent,int c,int r,string color,UIElement child){
   var b=new Border{Background=Ink.B(color),CornerRadius=new CornerRadius(17),Padding=new Thickness(11),Child=child,Margin=new Thickness(c==0?0:4,r==0?0:4,c==0?4:0,r==0?4:0)};
   cards.Add(b);Put(parent,b,c,r);return b;
  }
  UIElement Symbol(string kind,string color){
   var geometry=kind=="home"?"M3,11 L12,3 21,11 M5,10 L5,21 10,21 10,15 14,15 14,21 19,21 19,10":
    kind=="models"?"M5,5 L19,5 19,19 5,19 Z M9,9 L15,9 15,15 9,15 Z M8,1 L8,5 M16,1 L16,5 M8,19 L8,23 M16,19 L16,23 M1,8 L5,8 M1,16 L5,16 M19,8 L23,8 M19,16 L23,16":
    kind=="monitor"?"M3,20 L3,4 M3,20 L22,20 M6,15 L10,10 14,13 21,5":
    kind=="parameters"?"M3,6 L21,6 M3,12 L21,12 M3,18 L21,18 M8,3 L8,9 M16,9 L16,15 M10,15 L10,21":
    kind=="link"?"M9,15 L15,9 M8,17 L6,19 C2,23 -2,17 2,13 L7,8 C10,5 13,7 13,9 M11,15 C11,17 14,19 17,16 L22,11 C26,7 21,1 17,5 L15,7":
    "M4,16 L4,8 12,3 20,8 20,16 12,21 Z M8,14 L8,10 12,8 16,10 16,14 12,16 Z";
   return new Viewbox{Width=20,Height=20,Child=new System.Windows.Shapes.Path{Width=24,Height=24,Data=Geometry.Parse(geometry),Stroke=Ink.B(color),StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round}};
  }
  public void Navigate(int page){CurrentPage=Math.Max(0,Math.Min(4,page));Build();AnimateCards();if(CurrentPage==4){if(parameterSection==0){var ignored=LoadParameters();}else{var ignored=LoadPower();}}}
  public void ToggleTheme(){Prefs.Light=!Prefs.Light;Prefs.Save();Build();AnimateCards();}
  public void ToggleLanguage(){Prefs.Language=Prefs.Language=="en"?"zh":"en";Prefs.Save();Build();AnimateCards();}
  public void ToggleMotion(){Prefs.ReducedMotion=!Prefs.ReducedMotion;Prefs.Save();Build();AnimateCards();}
  public void TogglePause(){paused=!paused;Build();if(!paused){var ignored=Poll();}}
  void Build(){
   Title=L("SS本地模型管理器","SS Local Model Manager");Background=Ink.B(Bg);FontFamily=new FontFamily("Microsoft YaHei UI");
   texts.Clear();meters.Clear();gauges.Clear();cards.Clear();operationButtons.Clear();modelButtons.Clear();homeChoices.Clear();trend=null;
   Resources.Clear();
   Resources.Add(typeof(Button),(Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='Frame' Background='{TemplateBinding Background}' CornerRadius='9' Padding='{TemplateBinding Padding}' BorderThickness='1' BorderBrush='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
    <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Frame' Property='Opacity' Value='0.86'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Frame' Property='Opacity' Value='0.65'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Frame' Property='BorderBrush' Value='{Binding Foreground,RelativeSource={RelativeSource TemplatedParent}}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Frame' Property='Opacity' Value='0.42'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>"));
   Surface=GridOf(new[]{44.0,0},new[]{0.0});Surface.Width=640;Surface.Height=360;Surface.Background=Ink.B(Bg);
   var sidebar=new Grid{Background=Ink.B(Prefs.Light?"#E7E6E1":"#171719")};Put(Surface,sidebar);
   var logo=new Border{Width=29,Height=29,Background=Ink.B(Cyan),CornerRadius=new CornerRadius(10),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,12,0,0),Child=Symbol("logo",Dark)};sidebar.Children.Add(logo);
   var nav=new StackPanel{VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(5,58,5,0)};
   string[] navs={L("总览","Overview"),L("模型","Models"),L("监控","Monitor"),L("接口与日志","API & logs"),L("运行参数","Parameters")};
   string[] kinds={"home","models","monitor","link","parameters"};
   for(int i=0;i<5;i++){int page=i;var b=B(navs[i],()=>Navigate(page),"nav"+i,i==CurrentPage?Cyan:Prefs.Light?"#E7E6E1":"#171719",i==CurrentPage?Dark:Muted);b.Content=Symbol(kinds[i],i==CurrentPage?Dark:Muted);b.ToolTip=navs[i];b.Height=37;b.Margin=new Thickness(0,0,0,9);nav.Children.Add(b);}sidebar.Children.Add(nav);
   var motion=B(Prefs.ReducedMotion?"≈":"≋",ToggleMotion,"motion",Panel,Fore);motion.ToolTip=L(Prefs.ReducedMotion?"动效：减弱，点击开启强动效":"动效：强，点击减弱",Prefs.ReducedMotion?"Motion: reduced · click for full":"Motion: full · click to reduce");motion.Width=30;motion.Height=30;motion.VerticalAlignment=VerticalAlignment.Bottom;motion.Margin=new Thickness(0,0,0,14);sidebar.Children.Add(motion);
   var main=GridOf(new[]{0.0},new[]{37.0,0,20});main.Margin=new Thickness(11,7,12,7);Put(Surface,main,1);
   var header=GridOf(new[]{0.0,188},new[]{0.0});
   var title=T(L("SS本地模型管理器","MODEL CONSOLE"),17,null,true);title.ToolTip=L("拖动移动 · 双击最大化/还原 · 拖动边缘同步缩放","Drag to move · double-click to maximize · resize edges to scale");
   header.Background=Brushes.Transparent;AutomationProperties.SetAutomationId(header,"window-drag-region");
   header.MouseLeftButtonDown+=TitleDown;header.MouseMove+=TitleMove;header.MouseLeftButtonUp+=TitleUp;
   logo.MouseLeftButtonDown+=TitleDown;logo.MouseMove+=TitleMove;logo.MouseLeftButtonUp+=TitleUp;
   Put(header,title);
   var controls=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
   var lang=Small(Prefs.Language=="en"?"中":"EN",L("切换为英文","Switch to Chinese"),ToggleLanguage,"language");lang.Width=30;controls.Children.Add(lang);
   controls.Children.Add(Small(Prefs.Light?"◐":"◑",L("切换深色 / 浅色","Toggle dark / light"),ToggleTheme,"theme"));
   controls.Children.Add(Small(Topmost?"●":"○",L("窗口置顶","Always on top"),()=>{Topmost=!Topmost;Prefs.Topmost=Topmost;Prefs.Save();Build();},"pin"));
   pauseButton=Small(paused?"▶":"Ⅱ",L(paused?"继续监控":"暂停监控",paused?"Resume monitoring":"Pause monitoring"),TogglePause,"pause");controls.Children.Add(pauseButton);
   controls.Children.Add(Small("−",L("最小化","Minimize"),()=>WindowState=WindowState.Minimized,"minimize"));
   controls.Children.Add(Small("×",L("关闭窗口，模型继续运行","Close window; model keeps running"),Close,"close"));
   Put(header,controls,1);Put(main,header);
   var pageContent=CurrentPage==0?Overview():CurrentPage==1?Models():CurrentPage==2?Monitor():CurrentPage==3?Links():Parameters();
   Put(main,pageContent,0,1);
   var foot=GridOf(new[]{0.0,72},new[]{0.0});
   footer=T("",10,Muted);Put(foot,footer);var version=T("v"+ReleaseInfo.Version,10,Muted);version.HorizontalAlignment=HorizontalAlignment.Right;Put(foot,version,1);Put(main,foot,0,2);
   var view=new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.Both,Child=Surface};
   Content=view;Render();
  }
  ModelEntry Selected{get{return models.FirstOrDefault(x=>x.Id==selected);}}
  string CurrentName{get{var m=models.FirstOrDefault(x=>x.Alias==last.Model||x.Id==last.Model);return m!=null?m.Short:last.Online?last.Model:L("未运行","Offline");}}
  string Phase{get{return paused?L("已暂停","Paused"):busy?L("操作中","Working"):last.Phase=="generating"?L("生成中","Generating"):last.Phase=="reading"?L("预填中","Prefilling"):last.Phase=="processing"?L("处理中","Processing"):last.Phase=="loading"||service.Loading?L("加载中","Loading"):last.Phase=="reconnecting"?L("重连中","Reconnecting"):last.Online?L("待命","Ready"):L("已停止","Stopped");}}
  TextBlock Heading(string name,string color=Dark,double size=16){return T(name,size,color,true);}
  int ModelPageCount {get{return Math.Max(1,(models.Count+3)/4);}}
  public void ChangeModelPage(int delta){modelPage=Math.Max(0,Math.Min(ModelPageCount-1,modelPage+delta));Build();}
  Grid Overview(){
   var grid=GridOf(new[]{0.0,0},new[]{188.0,0});
   modelPage=Math.Max(0,Math.Min(ModelPageCount-1,modelPage));
   var engine=GridOf(new[]{0.0},new[]{28.0,70,22,0});
   var head=GridOf(new[]{0.0,94},new[]{0.0});var modelHeading=Heading(L("全部模型","ALL MODELS")+" · "+models.Count);modelHeading.Cursor=Cursors.Hand;modelHeading.ToolTip=L("打开可扩展模型库","Open the expandable model library");modelHeading.MouseLeftButtonUp+=(s,e)=>Navigate(1);Put(head,modelHeading);var pager=GridOf(new[]{26.0,42,26},new[]{0.0});
   var prev=B("‹",()=>ChangeModelPage(-1),"models-prev",Dark,"#FFFFFF");prev.IsEnabled=modelPage>0;prev.ToolTip=L("上一页","Previous page");
   var next=B("›",()=>ChangeModelPage(1),"models-next",Dark,"#FFFFFF");next.IsEnabled=modelPage<ModelPageCount-1;next.ToolTip=L("下一页","Next page");
   prev.Padding=next.Padding=new Thickness(0);prev.Height=next.Height=22;
   Put(pager,prev);var pageNumber=T((modelPage+1)+" / "+ModelPageCount,10,Dark,true);pageNumber.HorizontalAlignment=HorizontalAlignment.Center;Put(pager,pageNumber,1);Put(pager,next,2);Put(head,pager,1);Put(engine,head);
   var picks=GridOf(new[]{0.0,0},new[]{0.0,0});
   var homeModels=models.Skip(modelPage*4).Take(4).ToList();for(int i=0;i<homeModels.Count;i++){var entry=homeModels[i];Button b=null;b=B(entry.Short,()=>{if(!(b.Content is TextBox))SelectModel(entry.Id);},"model-"+i,selected==entry.Id?Dark:"#FF9486",selected==entry.Id?"#FFFFFF":Dark);
    b.Content=new TextBlock{Text=entry.Short,FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};b.ToolTip=entry.Display+"\n"+L("双击模型名改名","Double-click name to rename");b.MouseDoubleClick+=(sender,args)=>{args.Handled=true;RenameHome(entry,b);};homeChoices[b]=entry;b.Margin=new Thickness(i%2==0?0:3,1,i%2==0?3:0,3);b.IsEnabled=!busy;modelButtons.Add(b);Put(picks,b,i%2,i/2);}
   Put(engine,picks,0,1);Put(engine,Value("selected","",10,Dark),0,2);
   var actions=GridOf(new[]{0.0,0},new[]{0.0});var start=B(L("启动 / 切换","Start / switch"),()=>{var ignored=Operate(true);},"start",Dark,"#FFFFFF");var stop=B(L("停止服务","Stop service"),()=>{var ignored=Operate(false);},"stop","#FFAD9F",Dark);start.Margin=new Thickness(0,2,3,0);stop.Margin=new Thickness(3,2,0,0);Put(actions,start);Put(actions,stop,1);operationButtons.Add(start);operationButtons.Add(stop);Put(engine,actions,0,3);Card(grid,0,0,Coral,engine);
   var speed=GridOf(new[]{0.0},new[]{23.0,103,0});
   var speedHead=GridOf(new[]{0.0,100},new[]{0.0});Put(speedHead,Heading(L("推理吞吐","THROUGHPUT")));Put(speedHead,Value("speedKind","",10,Dark),1);Put(speed,speedHead);
   Put(speed,Dials(),0,1);trend=new Plot{Accent=Dark,Track="#7973BC",Floor=100,ToolTip=L("本次打开后采集的实时解码速度；空闲处留空，不把上次速度作为实时值","Live decode samples since opening; idle periods stay empty")};Put(speed,trend,0,2);Card(grid,1,0,Violet,speed);
   var hardware=GridOf(new[]{0.0},new[]{18.0,18,18,18});
   var hhead=GridOf(new[]{0.0,132},new[]{0.0});Put(hhead,Heading(L("显卡负载","GPU LOAD"),Dark,14));var hp=Value("powerOnly","",10,Dark);hp.HorizontalAlignment=HorizontalAlignment.Right;Put(hhead,hp,1);Put(hardware,hhead);
   Put(hardware,Bar("gpu",L("GPU","GPU"),Dark),0,1);Put(hardware,Bar("vram",L("显存","VRAM"),Dark),0,2);Put(hardware,Value("thermals","",10,Dark),0,3);Card(grid,0,1,Cyan,hardware);
   var context=GridOf(new[]{0.0},new[]{20.0,15,16,0});
   var chead=GridOf(new[]{0.0,115},new[]{0.0});Put(chead,Heading(L("上下文","CONTEXT"),Dark,14));var cap=Value("contextWindow","",10,Dark);cap.HorizontalAlignment=HorizontalAlignment.Right;Put(chead,cap,1);Put(context,chead);
   var meter=new Meter{Accent=Dark,Track="#789083",Height=7,VerticalAlignment=VerticalAlignment.Center};meters["context"]=meter;Put(context,meter,0,1);
   Put(context,Value("used","",10,Dark),0,2);var kv=Value("cacheScope","",10,"#485B4F");Put(context,kv,0,3);Card(grid,1,1,Sage,context);
   return grid;
  }
  Grid Dials(){
   var g=GridOf(new[]{0.0,0},new[]{0.0});
   var decode=new Gauge{Label=L("解码","Decode"),Accent=Dark,Track="#746EB7",Foreground=Dark};
   var prefill=new Gauge{Label=L("预填","Prefill"),Accent=Dark,Track="#746EB7",Foreground=Dark};
   gauges["decode"]=decode;gauges["prefill"]=prefill;Put(g,decode);Put(g,prefill,1);
   decode.ToolTip=L("解码速度，包含思考token；空闲显示上次请求。满刻度随读数调整，单位tok/s。","Decode including reasoning tokens; idle shows last request. Scale adapts, in tok/s.");
   prefill.ToolTip=L("实际处理输入token / 预填耗时；空闲显示上次请求，单位tok/s。","Actual input processing tokens / prefill time; idle shows last request, in tok/s.");return g;
  }
  Grid Bar(string key,string label,string color=null){
   var g=GridOf(new[]{38.0,0,66},new[]{0.0});Put(g,T(label,10,color??Fore));
   var m=new Meter{Height=7,Accent=color??Fore,Track=Muted,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(1,0,6,0)};meters[key]=m;Put(g,m,1);var value=Value(key,"—",10,color);value.HorizontalAlignment=HorizontalAlignment.Right;Put(g,value,2);return g;
  }
  Grid Models(){
   var outer=GridOf(new[]{0.0},new[]{26.0,0,42});
   var libraryHead=GridOf(new[]{0.0,64,64,56},new[]{0.0});Put(libraryHead,T(L("模型库 · ","LIBRARY · ")+models.Count,12,null,true));Put(libraryHead,B(L("扫描","Scan"),()=>{var ignored=ScanModels();},"scan-models"),1);Put(libraryHead,B(L("导入","Import"),()=>{var ignored=ImportModels();},"import-models"),2);Put(libraryHead,B(L("刷新","Refresh"),()=>ReloadModels(),"refresh-models"),3);Put(outer,libraryHead);
   var grid=GridOf(new[]{0.0,0},Enumerable.Repeat(114.0,(models.Count+1)/2).ToArray());
   for(int i=0;i<models.Count;i++){var m=models[i];string[] colors={Coral,Violet,Cyan,Sage};var color=colors[i%4];int row=i/2;
    var card=GridOf(new[]{0.0},new[]{24.0,18,18,0});var title=Heading(m.Short,Dark,14);title.Cursor=Cursors.IBeam;title.ToolTip=m.Display+"\n"+L("双击改名","Double-click to rename");title.MouseLeftButtonDown+=(sender,args)=>{if(args.ClickCount==2){args.Handled=true;Rename(m,title,card);}};Put(card,title);Put(card,T(m.Sub,10,Dark),0,1);
    Put(card,T((m.Context/1024)+"K "+L("上下文","context")+" · "+m.SizeText,10,Dark),0,2);
    var bottom=GridOf(new[]{0.0,58,46},new[]{0.0});Put(bottom,T(selected==m.Id?L("已选择","Selected"):L("可选择","Available"),10,Dark));
    var pick=B(L("选择","Select"),()=>SelectModel(m.Id),"select-"+i,Dark,"#FFFFFF");pick.IsEnabled=!busy;operationButtons.Add(pick);Put(bottom,pick,1);
    var rename=B(L("改名","Edit"),()=>Rename(m,title,card),"rename-"+i,color,Dark);rename.IsEnabled=!busy;operationButtons.Add(rename);Put(bottom,rename,2);Put(card,bottom,0,3);
    var border=Card(grid,i%2,row,color,card);border.Margin=new Thickness(i%2==0?0:4,0,i%2==0?4:0,8);
   }
   var scroll=new ScrollViewer{Content=grid,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,CanContentScroll=false};Put(outer,scroll,0,1);
   var actions=GridOf(new[]{0.0,110,100},new[]{0.0});actions.Margin=new Thickness(0,7,0,0);Put(actions,Value("selected","",11));
   var start=B(L("启动 / 切换","Start / switch"),()=>{var ignored=Operate(true);},"start",Coral,Dark);Put(actions,start,1);operationButtons.Add(start);
   var stop=B(L("停止服务","Stop service"),()=>{var ignored=Operate(false);},"stop");stop.Margin=new Thickness(6,0,0,0);Put(actions,stop,2);operationButtons.Add(stop);Put(outer,actions,0,2);return outer;
  }
  async Task ScanModels(){
   if(busy)return;busy=true;SetNotice("正在只读扫描已有模型…","Scanning existing models…");Build();
   try{int count=await Task.Run(()=>SS.Core.ModelDiscovery.ScanQuick());models=Registry.Load();registryStamp=File.GetLastWriteTimeUtc(Registry.ModelsJson);if(!models.Any(x=>x.Id==selected))selected=models.Count>0?models[0].Id:"";SetNotice("扫描完成，新增 "+count+" 个模型；仅登记引用，尚未验证运行。","Scan complete: "+count+" added; references only, runtime pending.");}
   catch(Exception ex){SetNotice("扫描未完成，请查看日志。","Scan did not complete; see logs.");LogError(ex);}
   finally{busy=false;Build();}
  }
  async Task ImportModels(){
   if(busy)return;var dialog=new Microsoft.Win32.OpenFileDialog{Title=L("选择GGUF主权重（分片请选择第一片）","Select GGUF (first shard)"),Filter="GGUF models|*.gguf",Multiselect=true};
   if(dialog.ShowDialog(this)!=true)return;busy=true;Build();
   try{int count=await Task.Run(()=>SS.Core.ModelDiscovery.AddFiles(dialog.FileNames));models=Registry.Load();if(!models.Any(x=>x.Id==selected))selected=models.Count>0?models[0].Id:"";SetNotice("已登记 "+count+" 个模型；原权重和外部配置保持。","Registered "+count+" models; source files preserved.");}
   catch(Exception ex){SetNotice("无法登记，请检查分片和格式。","Import failed; check format and shards.");LogError(ex);}finally{busy=false;Build();}
  }

  public void ReloadModels(){
   if(busy)return;var loaded=Registry.Load();if(loaded.Count==0){SetNotice("注册表不可读或暂无模型，请检查配置","Registry unavailable or empty; check config");Render();return;}
   models=loaded;registryStamp=File.GetLastWriteTimeUtc(Registry.ModelsJson);if(!models.Any(x=>x.Id==selected))selected=models[0].Id;notice="";Build();
  }
  void Rename(ModelEntry model,TextBlock title,Grid parent){
   if(busy)return;var edit=new TextBox{Text=model.Short,MaxLength=24,FontSize=13,VerticalContentAlignment=VerticalAlignment.Center};title.Visibility=Visibility.Hidden;Put(parent,edit);bool finished=false;
   Action<bool> finish=save=>{if(finished)return;finished=true;if(save){try{Registry.Rename(model.Id,edit.Text);model.Short=edit.Text.Trim();notice="";}catch(Exception){SetNotice("名称未保存，请输入1–24个字符并检查日志","Name not saved; use 1–24 characters and check logs");}}Build();};
   edit.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){e.Handled=true;finish(true);}else if(e.Key==Key.Escape){e.Handled=true;finish(false);}};edit.LostKeyboardFocus+=(s,e)=>finish(true);edit.Focus();edit.SelectAll();
  }
  void SelectModel(string id){if(busy)return;selected=id;notice="";if(CurrentPage==0){foreach(var pair in homeChoices){bool active=pair.Value.Id==id;pair.Key.Background=Ink.B(active?Dark:"#FF9486");pair.Key.Foreground=Ink.B(active?"#FFFFFF":Dark);}Render();}else Build();}
  void RenameHome(ModelEntry model,Button button){
   if(busy||button.Content is TextBox)return;
   var edit=new TextBox{Text=model.Short,MaxLength=24,FontSize=11,VerticalContentAlignment=VerticalAlignment.Center,Foreground=Ink.B(Dark),Background=Ink.B("#FFFFFF"),Padding=new Thickness(2,0,2,0)};
   AutomationProperties.SetAutomationId(edit,"rename-editor");button.Content=edit;bool finished=false;
   Action<bool> finish=save=>{if(finished)return;finished=true;if(save){try{Registry.Rename(model.Id,edit.Text);model.Short=edit.Text.Trim();notice="";}catch(Exception ex){SetNotice("名称未保存，请使用1–24个字符。","Name not saved; use 1–24 characters.");LogError(ex);}}Build();};
   edit.KeyDown+=(sender,args)=>{if(args.Key==Key.Enter){args.Handled=true;finish(true);}else if(args.Key==Key.Escape){args.Handled=true;finish(false);}};
   edit.LostKeyboardFocus+=(sender,args)=>finish(true);edit.Focus();edit.SelectAll();
  }
  int monitorSection;
  public void MonitorSection(int section){monitorSection=Math.Max(0,Math.Min(3,section));CurrentPage=2;Build();AnimateCards();}
  Grid Monitor(){
   var outer=GridOf(new[]{0.0},new[]{28.0,0});var tabs=GridOf(new[]{76.0,76,76,76,0},new[]{0.0});
   string[] names={L("推理","Inference"),L("硬件","Hardware"),L("服务","Services"),L("温度","Thermals")};
   for(int i=0;i<4;i++){int index=i;var b=B(names[i],()=>MonitorSection(index),"monitor-tab-"+i,i==monitorSection?Cyan:Panel,i==monitorSection?Dark:Fore);b.Margin=new Thickness(0,0,5,4);Put(tabs,b,i);}
   var phase=Value("monitorPhase","",10,Muted);phase.HorizontalAlignment=HorizontalAlignment.Right;Put(tabs,phase,4);Put(outer,tabs);
   Put(outer,monitorSection==0?Inference():monitorSection==1?Hardware():monitorSection==2?Services():Temperatures(),0,1);return outer;
  }
  Plot gpuPlot,cpuPlot,vramPlot,ramPlot;
  Grid Hardware(){
   var g=GridOf(new[]{0.0,0},new[]{0.0,0});
   gpuPlot=ResourceChart(g,0,0,"GPU", "gpuDetail",Cyan,100);
   cpuPlot=ResourceChart(g,1,0,"CPU", "cpuDetail",Sage,100);
   vramPlot=ResourceChart(g,0,1,L("显存","VRAM"),"vramDetail",Violet,12);
   ramPlot=ResourceChart(g,1,1,L("系统内存","SYSTEM RAM"),"ramDetail",Coral,64);return g;
  }
  Plot ResourceChart(Grid parent,int col,int row,string title,string key,string color,double floor){
   var g=GridOf(new[]{0.0},new[]{25.0,0,18});
   var head=GridOf(new[]{0.0,153},new[]{0.0});Put(head,Heading(title,Dark,14));var value=Value(key,"—",12,Dark,true);value.HorizontalAlignment=HorizontalAlignment.Right;Put(head,value,1);Put(g,head);
   var plot=new Plot{Accent=Dark,Track="#797A7B",Bars=key=="vramDetail"||key=="ramDetail",Floor=floor};Put(g,plot,0,1);
   Put(g,Value(key+"Foot","",10,Dark),0,2);Card(parent,col,row,color,g);return plot;
  }
  Plot coreTempPlot,hotspotPlot,memoryTempPlot,powerPlot;
  Grid Temperatures(){
   var g=GridOf(new[]{0.0,0},new[]{0.0,0});
   coreTempPlot=ResourceChart(g,0,0,L("核心温度","CORE TEMP"),"coreTemp",Cyan,100);
   hotspotPlot=ResourceChart(g,1,0,L("热点温度","HOTSPOT"),"hotspot",Coral,110);
   memoryTempPlot=ResourceChart(g,0,1,L("显存结温","VRAM TEMP"),"memoryTemp",Violet,110);
   powerPlot=ResourceChart(g,1,1,L("显卡功耗","GPU POWER"),"powerDetail",Sage,350);return g;
  }
  Grid Services(){
   var g=GridOf(new[]{0.0,0},new[]{0.0,0});
   var identity=GridOf(new[]{0.0},new[]{24.0,22,22,22,0});
   Put(identity,Heading(L("实际运行模型","ACTIVE MODEL"),Dark,14));Put(identity,Value("actualName","",13,Dark,true),0,1);Put(identity,Value("actualAlias","",10,Dark),0,2);Put(identity,Value("runtime","",11,Dark),0,3);Put(identity,Value("monitorPhase","",10,Dark),0,4);Card(g,0,0,Violet,identity);
   var servicePanel=GridOf(new[]{0.0},new[]{24.0,24,24,0});Put(servicePanel,Heading(L("端口与连接","PORTS & CONNECTION"),Dark,14));Put(servicePanel,Value("directService","",11,Dark),0,1);Put(servicePanel,Value("routerService","",11,Dark),0,2);Put(servicePanel,Value("sampleState","",10,Dark),0,3);Card(g,1,0,Cyan,servicePanel);
   var totals=GridOf(new[]{0.0},new[]{24.0,22,22,22,0});Put(totals,Heading(L("完整计数","TOKEN TOTALS"),Dark,14));Put(totals,Value("fullRequests","",12,Dark),0,1);Put(totals,Value("fullInput","",12,Dark),0,2);Put(totals,Value("fullOutput","",12,Dark),0,3);Put(totals,Value("scope","",10,Dark),0,4);Card(g,0,1,Coral,totals);
   var context=GridOf(new[]{0.0},new[]{24.0,22,22,22,0});Put(context,Heading(L("上下文与口径","CONTEXT & SCOPE"),Dark,14));Put(context,Value("fullContext","",11,Dark),0,1);Put(context,Value("fullUsed","",11,Dark),0,2);Put(context,Value("fullKv","",11,Dark),0,3);Put(context,Value("slots","",10,Dark),0,4);Card(g,1,1,Sage,context);return g;
  }
  string KvSummary(bool detailed=false){
   string value="";
   if(last.KvResidentCells.HasValue)value=L("KV驻留 ","KV resident cap ")+ConsoleMetrics.N(last.KvResidentCells/1024,"0.#")+"K / "+ConsoleMetrics.N(last.KvTotalCells/1024,"0.#")+"K";
   if(last.KvHostGiB.HasValue)value+=(value==""?"":" · ")+L("固定RAM ","Pinned RAM ")+ConsoleMetrics.N(last.KvHostGiB,"0.00")+" GiB";
   if(last.KvGpuMiB.HasValue)value+=(value==""?"":" · ")+L("KV分配GPU ","KV allocated GPU ")+ConsoleMetrics.N(last.KvGpuMiB,"0.0")+" MiB";
   if(last.KvCpuMiB.HasValue)value+=(value==""?"":" · ")+L("KV分配CPU ","KV allocated CPU ")+ConsoleMetrics.N(last.KvCpuMiB,"0.0")+" MiB";
   if(last.KvHitPercent.HasValue&&detailed)value+=(value==""?"":" · ")+L("KV读取命中 ","KV read hit ")+ConsoleMetrics.N(last.KvHitPercent,"0.0")+"%";
   if(value=="")value=last.Runtime=="Strata"?L("KV日志暂未提供读数","KV log reading unavailable"):L("KV上下文缓冲 ","KV context buffer ")+ConsoleMetrics.N(last.UsedTokens,"N0")+" tokens";
   if(detailed)value+="\n"+L("引擎日志/接口实报；驻留容量按注意力层，分配/缓冲不是动态已用字节。","Engine log/API facts; capacity is per attention layer. Allocation/buffer is not dynamic used bytes.");
   return value;
  }
  Grid Inference(){
   var grid=GridOf(new[]{0.0,0},new[]{0.0,0});
   var speed=GridOf(new[]{0.0},new[]{20.0,0});Put(speed,Heading(L("速度仪表","SPEED"),Dark,14));Put(speed,Dials(),0,1);Card(grid,0,0,Violet,speed);
   var totals=GridOf(new[]{0.0},new[]{20.0,36,16,16,0});Put(totals,Heading(L("推理统计","INFERENCE"),Dark,14));
   var counters=GridOf(new[]{0.0,0,0},new[]{0.0});string[] keys={"requests","input","output"};string[] labels={L("请求","Requests"),L("输入","Input"),L("输出","Output")};
   for(int i=0;i<3;i++){var p=new StackPanel();p.Children.Add(Value(keys[i],"—",18,Dark,true));p.Children.Add(T(labels[i],10,Dark));Put(counters,p,i);}Put(totals,counters,0,1);
   Put(totals,Value("average","",10,Dark),0,2);Put(totals,Value("ttft","",10,Dark),0,3);Put(totals,Value("scope","",10,Dark),0,4);Card(grid,1,0,Coral,totals);
   var hardware=GridOf(new[]{0.0},new[]{20.0,17,17,17,17,0});Put(hardware,Heading(L("本机资源","HOST RESOURCES"),Dark,14));Put(hardware,Bar("gpu","GPU",Dark),0,1);Put(hardware,Bar("vram",L("显存","VRAM"),Dark),0,2);Put(hardware,Bar("cpu","CPU",Dark),0,3);Put(hardware,Bar("ram",L("内存","RAM"),Dark),0,4);Put(hardware,Value("tempPower","",10,Dark),0,5);Card(grid,0,1,Cyan,hardware);
   var cache=GridOf(new[]{0.0},new[]{20.0,14,14,14,14,15,0});Put(cache,Heading(L("上下文与缓存","CONTEXT & CACHE"),Dark,14));
   Put(cache,Value("contextWindow","",10,Dark),0,1);Put(cache,Bar("context",L("占窗","Use"),Dark),0,2);Put(cache,Bar("prefix",L("前缀","Prefix"),Dark),0,3);Put(cache,Bar("draft",L("草稿","Draft"),Dark),0,4);
   var detail=Value("slots","",10,Dark);detail.ToolTip=L("没有排队指标时显示—；KV驻留容量与token缓冲不等于动态已用字节","No queue data is shown as —; capacity/token buffer is not dynamic used bytes");Put(cache,Value("kvSummary","",10,Dark),0,5);Put(cache,detail,0,6);Card(grid,1,1,Sage,cache);return grid;
  }
  Grid Links(){
   var grid=GridOf(new[]{0.0},new[]{51.0,51,51,0});
   LinkRow(grid,0,L("模型直连","Direct API"),SS.Core.AppPaths.DirectBaseUrl+"/v1",Cyan);
   LinkRow(grid,1,L("兼容接口","Compatible API"),SS.Core.AppPaths.DirectBaseUrl+"/v1",Violet);
   LinkRow(grid,2,L("所选模型 ID","Selected model ID"),Selected==null?"—":Selected.Alias,Sage);
   var lower=GridOf(new[]{0.0,0},new[]{0.0});
   var info=GridOf(new[]{0.0},new[]{24.0,0,24});Put(info,Heading(L("服务状态","SERVICES"),Dark,14));Put(info,Value("services","",11,Dark),0,1);var logs=B(L("打开日志目录 ↗","Open log folder ↗"),()=>OpenPath(Path.Combine(Registry.Root,"logs")),"logs",Dark,"#FFFFFF");Put(info,logs,0,2);Card(lower,0,0,Coral,info);
   var note=GridOf(new[]{0.0},new[]{24.0,0,24});Put(note,Heading(L("监控说明","MONITOR NOTES"),Fore,14));
   var wording=T(L("数据取自本机服务与显卡。\n上下文/前缀与KV日志实报。\n驻留容量/分配量不等于动态已用量。\n关闭窗口后模型继续运行。","Local service and GPU metrics.\nContext/prefix and KV log facts.\nCapacity/allocation are not dynamic use.\nClosing keeps the model running."),11,Muted);wording.TextWrapping=TextWrapping.Wrap;Put(note,wording,0,1);
   Button web=null;web=B(L("引擎监控网页 ↗","Engine monitor ↗"),()=>OpenPath((string)web.CommandParameter),"engine-monitor");web.CommandParameter=SS.Core.AppPaths.DirectBaseUrl+"/?engine=strata#monitor";engineButton=web;web.IsEnabled=last.Online&&last.Runtime=="Strata";web.ToolTip=L("仅Strata在线时可用","Available when Strata is online");Put(note,web,0,2);Card(lower,1,0,Panel,note);Put(grid,lower,0,3);return grid;
  }
  void LinkRow(Grid grid,int row,string label,string value,string color){
   var g=GridOf(new[]{91.0,0,52},new[]{0.0});Put(g,T(label,11,Dark,true));var text=T(value,11,Dark);text.ToolTip=value;Put(g,text,1);
   var copy=B(L("复制","Copy"),()=>{try{Clipboard.SetText(value);SetNotice("已复制","Copied");}catch{SetNotice("剪贴板暂不可用","Clipboard unavailable");}Render();},null,Dark,"#FFFFFF");Put(g,copy,2);
   var b=new Border{Background=Ink.B(color),CornerRadius=new CornerRadius(13),Padding=new Thickness(11,7,11,7),Margin=new Thickness(0,0,0,7),Child=g};cards.Add(b);Put(grid,b,0,row);
  }
  Dictionary<string,object> parameters;
  Dictionary<string,string> parameterEdits=new Dictionary<string,string>();
  int parameterSection;bool powerBusy,powerReading,powerTouched;int powerPercent=100;PowerState powerState;string powerStatus="";readonly IPowerDriver powerDriver=new NvidiaPowerDriver();Button powerApply,powerDefault;readonly Dictionary<int,Button> powerChoices=new Dictionary<int,Button>();
  bool parameterLoading;string parameterAttemptedAlias="";Button parameterSave,parameterRestart,parameterRefresh;
  static string PString(object row,string key){return InferenceMonitor.Adapter.Str(row,key);}
  string FieldLabel(object row){return PString(row,Prefs.Language=="en"?"en":"zh");}
  public void ParameterSection(int section){parameterSection=Math.Max(0,Math.Min(1,section));Build();if(parameterSection==1){var ignored=LoadPower();}else if(parameters==null){var ignored=LoadParameters();}}
  Grid Parameters(){
   var g=GridOf(new[]{0.0},new[]{26.0,0});var tabs=GridOf(new[]{100.0,100,0},new[]{0.0});
   var model=B(L("模型参数","Model settings"),()=>ParameterSection(0),"parameter-model",parameterSection==0?Cyan:Panel,parameterSection==0?Dark:Fore);
   var gpu=B(L("显卡功率","GPU power"),()=>ParameterSection(1),"parameter-power",parameterSection==1?Cyan:Panel,parameterSection==1?Dark:Fore);model.Margin=gpu.Margin=new Thickness(0,0,5,4);
   Put(tabs,model);Put(tabs,gpu,1);Put(g,tabs);Put(g,parameterSection==0?ModelParameters():PowerParameters(),0,1);return g;
  }
  UIElement RecommendationBadge(object row){
   var rec=InferenceMonitor.Adapter.Get(row,"recommendation");object value=InferenceMonitor.Adapter.Get(rec,"value");
   string number=value==null?"":Convert.ToDouble(value,CultureInfo.InvariantCulture).ToString("0.###",CultureInfo.InvariantCulture);
   var badge=new Border{CornerRadius=new CornerRadius(4),Background=Ink.B("#F7F7F2"),Padding=new Thickness(4,1,4,1),Margin=new Thickness(2,0,2,0),VerticalAlignment=VerticalAlignment.Center,Focusable=true,Cursor=Cursors.Help,Child=T(value==null?L("待评估","Review"):L("建议 ","Rec. ")+number,9,Dark,true)};
   AutomationProperties.SetAutomationId(badge,"recommend-"+PString(row,"key"));AutomationProperties.SetName(badge,FieldLabel(row)+" "+L("建议值 ","recommended ")+(value==null?L("待评估","not reviewed"):number));
   string why=PString(rec,Prefs.Language=="en"?"why_en":"why_zh"),status=PString(rec,Prefs.Language=="en"?"status_en":"status_zh");
   if(why=="")why=L("该模型的建议尚未评估。","Recommendations for this model have not been reviewed.");
   var body=new StackPanel{MaxWidth=370};body.Children.Add(T(FieldLabel(row)+" · "+(value==null?L("待评估","Not reviewed"):number),13,Fore,true));
   var explanation=T(why,11,Fore);explanation.TextWrapping=TextWrapping.Wrap;explanation.Margin=new Thickness(0,7,0,7);body.Children.Add(explanation);
   var context=T(L("本机：i7-12700F / 3080 Ti 12GB / 64GB RAM\n","PC: i7-12700F / 3080 Ti 12GB / 64GB RAM\n")+status+L("\n仅供参考，不会自动覆盖当前值。","\nReference only; does not replace the current value."),10,Muted);context.TextWrapping=TextWrapping.Wrap;body.Children.Add(context);
   var sources=InferenceMonitor.Adapter.List(InferenceMonitor.Adapter.Get(rec,"sources"));if(sources.Count>0){var source=T(L("依据：","Sources: ")+string.Join("\n",sources.Select(x=>Convert.ToString(x)).ToArray()),9,Muted);source.TextWrapping=TextWrapping.Wrap;source.Margin=new Thickness(0,6,0,0);body.Children.Add(source);}
   var tip=new ToolTip{Content=body,Background=Ink.B(Panel),BorderBrush=Ink.B(Muted),Foreground=Ink.B(Fore),Padding=new Thickness(12)};
   badge.ToolTip=tip;ToolTipService.SetInitialShowDelay(badge,250);ToolTipService.SetShowDuration(badge,60000);
   badge.GotKeyboardFocus+=(sender,e)=>tip.IsOpen=true;badge.LostKeyboardFocus+=(sender,e)=>tip.IsOpen=false;
   return badge;
  }
  Grid ModelParameters(){
   var grid=GridOf(new[]{0.0},new[]{44.0,0,49});
   var head=GridOf(new[]{0.0,78},new[]{0.0});
   var description=new StackPanel{VerticalAlignment=VerticalAlignment.Center};description.Children.Add(T(L("运行模型参数","RUNNING MODEL PARAMETERS"),15,null,true));
   description.Children.Add(Value("parameterIdentity",last.Online?CurrentName+" · "+last.Runtime+" · "+ConsoleMetrics.N(last.Context/1024)+"K":L("请先启动一个模型","Start a model first"),10,Muted));Put(head,description);
   parameterRefresh=B(L("重新读取","Reload"),()=>{var ignored=LoadParameters();},"parameter-refresh",Cyan,Dark);parameterRefresh.Height=28;Put(head,parameterRefresh,1);Put(grid,head);
   var fields=InferenceMonitor.Adapter.List(InferenceMonitor.Adapter.Get(parameters,"fields"));
   if(fields.Count==0){
    var empty=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
    empty.Children.Add(T(parameterLoading?L("正在读取实际模型配置…","Reading active model settings…"):L("启动模型后读取它的参数","Load settings after starting a model"),14,null,true));
    empty.Children.Add(T(L("保存配置不会立即中断推理。","Saving settings keeps the current service running."),11,Muted));
    Put(grid,empty,0,1);
   }else{
    var list=GridOf(new[]{0.0,0},Enumerable.Repeat(69.0,(fields.Count+1)/2).ToArray());int index=0;
    foreach(var row in fields){
     int i=index++;string key=PString(row,"key"),initial;double value=InferenceMonitor.Adapter.Num(row,"value")??0;
     if(!parameterEdits.TryGetValue(key,out initial))initial=Convert.ToDouble(InferenceMonitor.Adapter.Get(row,"value"),CultureInfo.InvariantCulture).ToString("0.###",CultureInfo.InvariantCulture);
     var card=GridOf(new[]{0.0,67},new[]{25.0,0});string[] colors={Violet,Cyan,Sage,Coral};string color=colors[(i/2)%4];
     var label=GridOf(new[]{0.0,72},new[]{0.0});var fieldName=T(FieldLabel(row),11,Dark,true);fieldName.ToolTip=FieldLabel(row);Put(label,fieldName);Put(label,RecommendationBadge(row),1);Put(card,label);
     var input=new TextBox{Text=initial,FontSize=12,VerticalContentAlignment=VerticalAlignment.Center,HorizontalContentAlignment=HorizontalAlignment.Right,Padding=new Thickness(3,0,3,0),BorderThickness=new Thickness(0),Background=Ink.B("#F7F7F2"),Foreground=Ink.B(Dark),Margin=new Thickness(4,0,0,1)};
     AutomationProperties.SetAutomationId(input,"parameter-"+key);AutomationProperties.SetName(input,FieldLabel(row));Put(card,input,1);
     double lo=Convert.ToDouble(InferenceMonitor.Adapter.Get(row,"min"),CultureInfo.InvariantCulture),hi=Convert.ToDouble(InferenceMonitor.Adapter.Get(row,"max"),CultureInfo.InvariantCulture);
     bool whole=InferenceMonitor.Adapter.Get(row,"integer") is bool&&(bool)InferenceMonitor.Adapter.Get(row,"integer");
     double current;if(!double.TryParse(initial,NumberStyles.Float,CultureInfo.InvariantCulture,out current))current=value;
     var slider=new Slider{Minimum=lo,Maximum=hi,Value=Math.Max(lo,Math.Min(hi,current)),TickFrequency=whole?1:.01,IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,3,0),ToolTip=lo.ToString(CultureInfo.InvariantCulture)+" – "+hi.ToString(CultureInfo.InvariantCulture)};
     Grid.SetColumnSpan(slider,2);Put(card,slider,0,1);bool syncing=false;
     slider.ValueChanged+=(s,e)=>{if(syncing)return;syncing=true;input.Text=slider.Value.ToString(whole?"0":"0.00",CultureInfo.InvariantCulture);parameterEdits[key]=input.Text;syncing=false;};
     input.TextChanged+=(s,e)=>{if(syncing)return;parameterEdits[key]=input.Text;double n;if(double.TryParse(input.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&n>=lo&&n<=hi){syncing=true;slider.Value=n;syncing=false;}};
     input.IsEnabled=!busy&&!parameterLoading;slider.IsEnabled=input.IsEnabled;
     var border=Card(list,i%2,i/2,color,card);border.Padding=new Thickness(9,5,9,5);border.Margin=new Thickness(i%2==0?0:4,0,i%2==0?4:0,7);
    }
    Put(grid,new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,CanContentScroll=false},0,1);
   }
   var actions=GridOf(new[]{0.0,106,115},new[]{18.0,0});var note=T(L("配置值 · 下次启动生效；上下文按接入配置管理。","Saved defaults · Restart to apply. Context stays managed."),10,Muted);Grid.SetColumnSpan(note,3);Put(actions,note);
   var source=T(parameters!=null?PString(parameters,"source"):L("仅调整实际运行模型","Actual model only"),10,Muted);source.ToolTip=source.Text;Put(actions,source,0,1);
   parameterSave=B(L("保存配置","Save settings"),()=>{var ignored=SaveParameters(false);},"parameter-save",Sage,Dark);parameterSave.Margin=new Thickness(0,2,5,0);Put(actions,parameterSave,1,1);
   parameterRestart=B(L("保存并重启","Save & restart"),()=>{var ignored=SaveParameters(true);},"parameter-restart",Coral,Dark);parameterRestart.Margin=new Thickness(0,2,0,0);Put(actions,parameterRestart,2,1);Put(grid,actions,0,2);return grid;
  }
  bool PowerAllowed(int percent){try{PowerPolicy.Validate(powerState,powerState,percent);return !busy&&!powerBusy&&!powerReading;}catch{return false;}}
  string PowerMessage(string code){
   switch(code){
    case "power_applied":return L("功率上限已应用并回读确认。","Power limit applied and verified.");
    case "power_unchanged":return L("当前已是该档位，无需修改。","Already at this preset; nothing changed.");
    case "power_cancelled":return L("已取消管理员授权；请查看当前回读值。","Administrator approval cancelled; see current readback.");
    case "power_temperature_guard":return L("温度偏高或三温度读数不完整，禁止提高功率；可选择更低档位。","Temperature high or incomplete; increases blocked. Lower presets remain available.");
    case "power_gpu_changed":case "power_settings_changed":return L("显卡或功率设置已变化，请刷新后重试。","GPU or power settings changed; refresh and retry.");
    case "power_busy":return L("另一项功率操作尚未完成。","Another power operation is pending.");
    case "power_unsupported":case "power_query_failed":case "power_tool_missing":return L("无法确认驱动功率范围，已禁止修改。","Driver limits unavailable; changes disabled.");
    default:return L("未能确认应用成功，请刷新核对；不显示为已应用。","Application could not be verified; refresh to check.");
   }
  }
  Grid PowerParameters(){
   powerChoices.Clear();var g=GridOf(new[]{0.0},new[]{42.0,62,23,34,31,0});
   var head=GridOf(new[]{0.0,72},new[]{20.0,20});Put(head,T(L("显卡功率上限","GPU POWER LIMIT"),15,null,true));Put(head,Value("powerIdentity","",10,Muted),0,1);
   var refresh=B(L("刷新","Refresh"),()=>{var ignored=LoadPower();},"power-refresh");refresh.IsEnabled=!powerBusy&&!powerReading;Grid.SetRowSpan(refresh,2);Put(head,refresh,1);Put(g,head);
   var picks=GridOf(new[]{0.0,0,0,0,0},new[]{0.0});
   for(int i=0;i<PowerPolicy.Presets.Length;i++){int percent=PowerPolicy.Presets[i];int? watts=null;try{watts=PowerPolicy.Target(powerState,percent);}catch{}
    var label=new StackPanel{VerticalAlignment=VerticalAlignment.Center};label.Children.Add(T(percent+"%",17,percent==powerPercent?Dark:Fore,true));
    label.Children.Add(T(watts.HasValue?watts.Value+" W":"—",11,percent==powerPercent?Dark:Muted));
    var b=B("",()=>{powerPercent=percent;powerTouched=true;powerStatus="";Build();},"power-preset-"+percent,percent==powerPercent?Cyan:Panel,percent==powerPercent?Dark:Fore);
    b.Content=label;b.Margin=new Thickness(i==0?0:3,0,i==4?0:3,3);b.IsEnabled=watts.HasValue&&!powerBusy&&!busy;b.ToolTip=L("默认功率上限的","Factory default limit × ")+percent+"%"+L("；选择后点击应用。","; choose then apply.");powerChoices[percent]=b;Put(picks,b,i);
   }
   Put(g,picks,0,1);Put(g,Value("powerCurrent","",12,null,true),0,2);
   var status=Value("powerStatus","",10,Muted);status.TextWrapping=TextWrapping.Wrap;Put(g,status,0,3);
   var actions=GridOf(new[]{0.0,0},new[]{0.0});powerApply=B(L("应用所选档位","Apply selected preset"),()=>{var ignored=ApplyPower(powerPercent);},"power-apply",Cyan,Dark);
   powerDefault=B(L("恢复默认 · 100%","Restore default · 100%"),()=>{var ignored=ApplyPower(100);},"power-default",Sage,Dark);powerApply.Margin=new Thickness(0,0,4,0);powerDefault.Margin=new Thickness(4,0,0,0);Put(actions,powerApply);Put(actions,powerDefault,1);Put(g,actions,0,4);
   var note=T(L("整卡功率上限，瞬时采样可波动；只点击应用才修改。\nWindows可能请求管理员授权；关闭窗口不自动恢复。","Whole-GPU ceiling; transient readings can vary. Applies only on click.\nWindows may request administrator approval; closing keeps the limit."),9,Muted);note.TextWrapping=TextWrapping.Wrap;Put(g,note,0,5);
   return g;
  }
  async Task LoadPower(){
   if(powerBusy||powerReading)return;powerReading=true;int gen=generation;
   if(CurrentPage==4&&parameterSection==1)Build();
   try{var read=await Task.Run(()=>powerDriver.Read());if(!closed&&gen==generation&&!powerBusy){powerState=read;powerStatus="";if(!powerTouched)foreach(int p in PowerPolicy.Presets)if(Math.Abs(PowerPolicy.Target(read,p)-read.Limit.Value)<.5)powerPercent=p;}}
   catch(Exception ex){if(!closed){powerState=null;powerStatus=PowerMessage(ex.Message);}}
   finally{powerReading=false;if(!closed&&CurrentPage==4&&parameterSection==1)Build();}
  }
  async Task ApplyPower(int percent){
   if(!PowerAllowed(percent)||closed)return;powerPercent=percent;powerTouched=true;var expected=powerState.Copy();
   powerBusy=true;generation++;powerStatus=L("等待驱动应用 / 管理员授权，请稍候…","Waiting for driver / administrator approval…");Build();
   try{var result=await Task.Run(()=>new PowerController(powerDriver).Apply(expected,percent));if(closed)return;powerState=result.Actual;powerStatus=PowerMessage(result.Code);
    if(!result.Success)LogError(new InvalidOperationException("GPU power operation: "+result.Code));
    if(!result.Success)powerStatus+=result.FallbackConfirmed?(result.RestoredPrevious?L(" 已确认恢复原上限。"," Previous limit restored."):L(" 已确认保留较低上限。"," A lower limit was verified.")):L(" 请以回读值为准。"," Check the actual readback.");
   }catch(Exception ex){if(!closed){powerState=null;powerStatus=PowerMessage(ex.Message);LogError(ex);}}
   finally{powerBusy=false;generation++;if(!closed)Build();}await Poll();
  }
  async Task LoadParameters(){
   if(parameterLoading||busy||!last.Online)return;parameterLoading=true;parameters=null;parameterEdits.Clear();string alias=last.Model;parameterAttemptedAlias=alias;Build();
   try{var payload=new Dictionary<string,object>{{"action","load"},{"alias",alias}};var loaded=await Task.Run(()=>ParameterClient.Call(payload));if(closed)return;if(last.Model!=alias)throw new InvalidOperationException("model_changed");parameters=loaded;notice="";}
   catch(Exception ex){if(!closed){SetNotice("读取失败，请确认实际模型在线。","Could not read settings; verify the active model.");LogError(ex);}}
   finally{parameterLoading=false;if(!closed)Build();}
  }
  async Task SaveParameters(bool restart){
   if(powerBusy||busy||parameterLoading||parameters==null||!last.Online)return;
   if(PString(parameters,"alias")!=last.Model){SetNotice("模型已切换，请重新读取。","Model changed; reload settings.");Render();return;}
   string alias=last.Model;var changes=new Dictionary<string,object>();foreach(var entry in parameterEdits)changes[entry.Key]=entry.Value;
   var payload=new Dictionary<string,object>{{"action","save"},{"alias",alias},{"fingerprint",InferenceMonitor.Adapter.Get(parameters,"fingerprint")},{"changes",changes},{"restart",restart}};
   busy=true;generation++;SetNotice(restart?"正在保存并重启当前模型…":"正在保存配置…",restart?"Saving and restarting active model…":"Saving settings…");Build();
   try{
    var result=await Task.Run(()=>ParameterClient.Call(payload));parameters=null;parameterEdits.Clear();parameterAttemptedAlias="";
    if(result.ContainsKey("error"))SetNotice("配置已保留，但重启未完成，请查看日志。","Settings retained, but restart failed; check logs.");else if(InferenceMonitor.Adapter.Get(result,"unchanged") is bool&&(bool)InferenceMonitor.Adapter.Get(result,"unchanged"))SetNotice("参数未变化，无需重启。","No changes; restart not needed.");
    else if(InferenceMonitor.Adapter.Get(result,"restarted") is bool&&(bool)InferenceMonitor.Adapter.Get(result,"restarted"))SetNotice("配置已保存，正在重启加载…","Settings saved; restarted model is loading…");
    else SetNotice("配置已备份并保存，下次启动生效。","Backed up and saved; applies on next start.");
   }catch(Exception ex){SetNotice(ex.Message.Contains("config_saved")?"配置已保留，但重启未完成，请查看日志。":"参数未保存，请重新读取并检查范围或模型状态。",ex.Message.Contains("config_saved")?"Settings retained, but restart failed; check logs.":"Settings not saved; reload and check ranges / model state.");LogError(ex);}
   finally{busy=false;generation++;if(!closed)Build();}await Poll();
  }
  void OpenPath(string path){try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch{SetNotice("无法打开，请检查日志","Could not open; check logs");Render();}}
  void SetMeter(string key,double? percentage,string label){Meter m;if(meters.TryGetValue(key,out m))m.Set(percentage,Prefs.ReducedMotion);Text(key,label);}
  void Render(){
   if(engineButton!=null&&CurrentPage==3)engineButton.IsEnabled=last.Online&&last.Runtime=="Strata";
   Text("selected",Selected==null?L("尚未添加模型，请到模型库扫描或导入","No models yet; scan or import in Library"):Selected.Short+" · "+(Selected.Context/1024)+"K "+L("上下文","context"));
   Text("speedKind",last.Decode.HasValue||last.Prefill.HasValue?L("实时 · tok/s","LIVE · tok/s"):L("上次请求","Last request"));
   foreach(var pair in gauges){bool decode=pair.Key=="decode";double? val=decode?last.Decode??last.LastDecode:last.Prefill??last.LastPrefill;double step=decode?100:2000;pair.Value.Label=(decode?L("解码","Decode"):L("预填","Prefill"))+( (decode?last.Decode:last.Prefill).HasValue?L("·实时","·Live"):val.HasValue?L("·上次","·Last"):L("·暂无","·None"));pair.Value.Set(val,Math.Max(step,Math.Ceiling((val??0)/step)*step),Prefs.ReducedMotion);AutomationProperties.SetName(pair.Value,pair.Value.Label+" "+ConsoleMetrics.N(val,"0.0")+" tok/s");}
   if(trend!=null){trend.Set(history.Decode,freshHistory&&!Prefs.ReducedMotion);freshHistory=false;}
   Text("tempPower",ConsoleMetrics.N(last.Temp)+"°C  /  "+ConsoleMetrics.N(last.Power)+" W");
   SetMeter("gpu",last.GpuUtil,ConsoleMetrics.N(last.GpuUtil)+"%");
   SetMeter("vram",ConsoleMetrics.Ratio(last.VramUsed,last.VramTotal),ConsoleMetrics.N(last.VramUsed,"0.0")+" / "+ConsoleMetrics.N(last.VramTotal,"0")+"G");
   SetMeter("cpu",host.Cpu,ConsoleMetrics.N(host.Cpu)+"%");
   SetMeter("ram",ConsoleMetrics.Ratio(host.RamUsed,host.RamTotal),ConsoleMetrics.N(host.RamUsed,"0.0")+" / "+ConsoleMetrics.N(host.RamTotal,"0")+"G");
   Text("contextWindow",last.Context.HasValue?ConsoleMetrics.N(last.Context/1024,"0")+"K "+L("窗口","window"):L("窗口 —","Window —"));
   double? ratio=ConsoleMetrics.Ratio(last.UsedTokens,last.Context);
   SetMeter("context",ratio,ConsoleMetrics.N(ratio,"0.0")+(ratio.HasValue?"%":""));
   // The compact card uses a header for window size and a separate request label.
   Text("used",(last.Runtime=="Strata"?L("上次请求 ","Last request "):L("槽位上下文 ","Slot context "))+ConsoleMetrics.Count(last.UsedTokens)+" tokens · "+ConsoleMetrics.N(ratio,"0.0")+(ratio.HasValue?"%":""));
   Text("cacheScope",KvSummary());Text("kvSummary",KvSummary());
   foreach(string key in new[]{"cacheScope","kvSummary","fullKv"}){TextBlock tip;if(texts.TryGetValue(key,out tip))tip.ToolTip=KvSummary(true);}

   Text("requests",ConsoleMetrics.Count(last.Requests));Text("input",ConsoleMetrics.Count(last.Input));Text("output",ConsoleMetrics.Count(last.Output));
   Text("average",L("均速 / 峰值 ","Avg / peak ")+ConsoleMetrics.N(last.Average,"0.0")+" / "+ConsoleMetrics.N(last.Peak,"0.0")+" tok/s");
   Text("ttft",(last.TtftKind=="首推理 token"?L("首推理 token ","First engine token "):L("首正文 ","First content "))+ConsoleMetrics.N(last.TtftMs)+" ms");
   Text("scope",last.Online?(last.Runtime=="Strata"?L("当前服务累计","Current service totals"):L("监控观察累计 · 未缓存输入","Observed totals · uncached input")):L("启动后自动采样","Auto-connects after start"));
   SetMeter("prefix",last.Prefix,ConsoleMetrics.N(last.Prefix)+(last.Prefix.HasValue?"%":""));
   SetMeter("draft",last.Draft,ConsoleMetrics.N(last.Draft)+(last.Draft.HasValue?"%":""));
   Text("slots",L("槽位 ","Slots ")+ConsoleMetrics.N(last.ActiveSlots)+"/"+ConsoleMetrics.N(last.Slots)+L(" · 排队 "," · Queue ")+ConsoleMetrics.N(last.Queued)+(last.KvHitPercent.HasValue?L(" · 上次KV命中 "," · Last KV hit ")+ConsoleMetrics.N(last.KvHitPercent,"0.0")+"%":""));
   Text("services",SS.Core.AppPaths.DirectPort+" "+(service.Model?L("在线","online"):service.Loading?L("加载中","loading"):L("离线","offline"))+"  ·  API "+(service.Router?L("在线","online"):L("离线","offline")));
   Text("monitorPhase",Phase+" · "+(updated==default(DateTime)?"—":updated.ToString("HH:mm:ss")));
   Text("actualName",CurrentName);Text("actualAlias",last.Online?last.Model:L("无实际运行模型","No active model"));Text("runtime",L("引擎：","Runtime: ")+(last.Runtime!=""?last.Runtime:"—"));
   Text("directService",SS.Core.AppPaths.DirectPort+" · "+(service.Model?L("健康","Healthy"):service.Loading?L("加载中","Loading"):L("未运行 / 不可达","Stopped / unreachable")));
   Text("routerService","API · "+(service.Router?L("在线","Online"):L("离线","Offline"))+" · "+(service.Router?(service.Mode=="cloud"?L("云端兜底","Cloud fallback"):L("本地优先","Local first")):"—"));
   Text("sampleState",L("采样：","Sample: ")+(paused?L("暂停","Paused"):updated==default(DateTime)?L("等待","Waiting"):updated.ToString("yyyy-MM-dd HH:mm:ss")));
   Text("fullRequests",L("请求数   ","Requests   ")+ConsoleMetrics.N(last.Requests,"N0"));
   Text("fullInput",L("输入 tokens   ","Input tokens   ")+ConsoleMetrics.N(last.Input,"N0"));
   Text("fullOutput",L("输出 tokens   ","Output tokens   ")+ConsoleMetrics.N(last.Output,"N0"));
   Text("fullContext",L("窗口   ","Window   ")+ConsoleMetrics.N(last.Context,"N0")+" tokens");
   Text("fullUsed",(last.Runtime=="Strata"?L("上次请求   ","Last request   "):L("槽位上下文   ","Slot context   "))+ConsoleMetrics.N(last.UsedTokens,"N0")+" tokens");
   Text("fullKv",KvSummary());TextBlock kvTip;if(texts.TryGetValue("fullKv",out kvTip))kvTip.ToolTip=KvSummary(true);
   Text("gpuDetail",ConsoleMetrics.N(last.GpuUtil)+"%");Text("cpuDetail",ConsoleMetrics.N(host.Cpu)+"%");
   Text("vramDetail",ConsoleMetrics.N(last.VramUsed,"0.00")+" / "+ConsoleMetrics.N(last.VramTotal,"0")+" GiB");
   Text("ramDetail",ConsoleMetrics.N(host.RamUsed,"0.0")+" / "+ConsoleMetrics.N(host.RamTotal,"0")+" GiB");
   Text("gpuDetailFoot",ConsoleMetrics.N(last.Temp)+"°C · "+ConsoleMetrics.N(last.Power)+" W");
   Text("cpuDetailFoot","Intel i7-12700F · "+L("本机实时采样","Live system sample"));
   Text("vramDetailFoot",last.GpuName+" · "+L("整卡占用","Whole GPU"));
   Text("ramDetailFoot",L("物理内存 · 系统全部程序","Physical RAM · All processes"));
   if(CurrentPage==2&&monitorSection==1){if(gpuPlot!=null)gpuPlot.Set(history.Gpu,false);if(cpuPlot!=null)cpuPlot.Set(history.Cpu,false);if(vramPlot!=null)vramPlot.Set(history.Vram,false);if(ramPlot!=null)ramPlot.Set(history.Ram,false);}
   double? core=thermals.Core??last.Temp;
   string temperatures=L("核心","Core ")+ConsoleMetrics.N(core)+"° · "+L("热点","Hot ")+ConsoleMetrics.N(thermals.Hotspot)+"° · "+L("显存","Mem ")+ConsoleMetrics.N(thermals.Memory)+"°";
   Text("powerOnly",ConsoleMetrics.N(last.Power)+" W");Text("thermals",temperatures);
   Text("tempPower",temperatures+" · "+ConsoleMetrics.N(last.Power)+" W");
   Text("gpuDetailFoot",temperatures+" · "+ConsoleMetrics.N(last.Power)+" W");
   Text("coreTemp",ConsoleMetrics.N(core,"0.0")+" °C");Text("hotspot",ConsoleMetrics.N(thermals.Hotspot,"0.0")+" °C");Text("memoryTemp",ConsoleMetrics.N(thermals.Memory,"0.0")+" °C");Text("powerDetail",ConsoleMetrics.N(last.Power)+" W");
   Text("coreTempFoot",L("GPU 芯片核心 · 独立传感器","GPU core · Independent sensor"));
   Text("hotspotFoot",L("GPU 芯片热点 · 独立传感器","GPU hotspot · Independent sensor"));
   Text("memoryTempFoot",L("GDDR6X 显存结温 · 独立传感器","GDDR6X junction · Independent sensor"));
   Text("powerDetailFoot",L("整卡功耗 · NVIDIA 采样","Whole GPU · NVIDIA reading"));
   if(CurrentPage==2&&monitorSection==3){coreTempPlot.Set(history.CoreTemp,false);hotspotPlot.Set(history.Hotspot,false);memoryTempPlot.Set(history.MemoryTemp,false);powerPlot.Set(history.Power,false);}
   if(CurrentPage==4&&parameterSection==0){Text("parameterIdentity",last.Online?CurrentName+" · "+last.Runtime+" · "+ConsoleMetrics.N(last.Context/1024)+"K":L("请先启动一个模型","Start a model first"));bool ready=last.Online&&!busy&&!powerBusy&&!parameterLoading&&parameters!=null&&PString(parameters,"alias")==last.Model;parameterSave.IsEnabled=ready;parameterRestart.IsEnabled=ready;parameterRefresh.IsEnabled=last.Online&&!busy&&!parameterLoading;}
   foreach(var b in operationButtons)b.IsEnabled=!busy&&!powerBusy&&models.Count>0;
   if(CurrentPage==4&&parameterSection==1){
    bool supported=PowerPolicy.Supported(powerState);
    Text("powerIdentity",supported?powerState.Name+" · "+L("默认 ","Default ")+ConsoleMetrics.N(powerState.Default)+"W · "+L("允许 ","Allowed ")+ConsoleMetrics.N(powerState.Min)+"–"+ConsoleMetrics.N(powerState.Default)+"W":L("等待显卡功率数据；暂不允许修改","Waiting for GPU limits; changes disabled"));
    Text("powerCurrent",supported?L("当前上限 ","Current limit ")+ConsoleMetrics.N(powerState.Limit)+"W ("+ConsoleMetrics.N(powerState.Limit/powerState.Default*100,"0.#")+"%) · "+L("实时 ","Draw ")+ConsoleMetrics.N(powerState.Draw)+"W":L("当前上限 —","Current limit —"));
    string temps=supported?L("核心/热点/显存 ","Core/hotspot/memory ")+ConsoleMetrics.N(powerState.Core)+"/"+ConsoleMetrics.N(powerState.Hotspot)+"/"+ConsoleMetrics.N(powerState.Memory)+"°C · ":"";
    Text("powerStatus",temps+(powerStatus!=""?powerStatus:powerReading?L("正在读取…","Reading…"):PowerAllowed(powerPercent)?L("已选 ","Selected ")+powerPercent+"% · "+L("点击应用后生效","Click Apply to change"):PowerMessage("power_temperature_guard")));
    if(powerApply!=null)powerApply.IsEnabled=PowerAllowed(powerPercent);if(powerDefault!=null)powerDefault.IsEnabled=PowerAllowed(100);
   }
   if(footer!=null){footer.Text=sampleError?L("采样暂不可用，将自动重试","Sampling unavailable; retrying"):notice!=""?notice:paused?L("已暂停于 ","Paused at ")+updated.ToString("HH:mm:ss"):L("实际模型：","Running: ")+CurrentName+" · "+Phase+" · "+(updated==default(DateTime)?L("正在采样","Sampling"):updated.ToString("HH:mm:ss"));footer.ToolTip=footer.Text;}
  }
  void AnimateCards(){
   if(Prefs.ReducedMotion)return;for(int i=0;i<cards.Count;i++){var card=cards[i];card.RenderTransformOrigin=new Point(.5,.5);var group=new TransformGroup();var move=new TranslateTransform(0,14);var scale=new ScaleTransform(.965,.965);group.Children.Add(scale);group.Children.Add(move);card.RenderTransform=group;
    TimeSpan delay=TimeSpan.FromMilliseconds(i*65);var spring=new DoubleAnimation(0,TimeSpan.FromMilliseconds(620)){BeginTime=delay,EasingFunction=new BackEase{Amplitude=.25,EasingMode=EasingMode.EaseOut}};move.BeginAnimation(TranslateTransform.YProperty,spring);
    var size=new DoubleAnimation(1,TimeSpan.FromMilliseconds(620)){BeginTime=delay,EasingFunction=new BackEase{Amplitude=.35,EasingMode=EasingMode.EaseOut}};scale.BeginAnimation(ScaleTransform.ScaleXProperty,size);scale.BeginAnimation(ScaleTransform.ScaleYProperty,size);
    card.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(360)){BeginTime=delay});
   }
  }
  public async Task Poll(){
   if(polling||paused||closed)return;polling=true;int gen=generation;
   try{
    var gpu=CurrentPage==4&&parameterSection==1&&!powerBusy&&!powerReading?Task.Run(()=>{try{return powerDriver.Read();}catch{return null;}}):null;
    var telemetry=Task.Run(()=>{var s=collector.Poll();host.Poll();thermals.Poll();return s;});var backend=Task.Run(()=>Backend.Probe());if(gpu!=null)await Task.WhenAll(telemetry,backend,gpu);else await Task.WhenAll(telemetry,backend);
    if(closed||paused||gen!=generation)return;last=telemetry.Result;service=backend.Result;if(service.Ready&&(noticeZh=="正在加载，请稍候…"||noticeZh=="配置已保存，正在重启加载…"))notice="";if(gpu!=null&&!powerBusy)powerState=gpu.Result;updated=DateTime.Now;sampleError=false;if(!busy&&File.GetLastWriteTimeUtc(Registry.ModelsJson)!=registryStamp)ReloadModels();
    history.Add(last,(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds,host,thermals,last.Power);
    if(!last.Online)parameterAttemptedAlias="";
    if(parameters!=null&&PString(parameters,"alias")!=last.Model){parameters=null;parameterEdits.Clear();if(CurrentPage==4)Build();}
    Render();if(CurrentPage==4&&parameterSection==0&&last.Online&&!busy&&!parameterLoading&&parameters==null&&parameterAttemptedAlias!=last.Model){var ignored=LoadParameters();}
   }catch(Exception ex){if(!closed){sampleError=true;LogError(ex);Render();}}finally{polling=false;}
  }
  async Task Operate(bool start){
   if(powerBusy||busy||(start&&Selected==null))return;string target=selected;busy=true;generation++;SetNotice(start?"正在加载，请稍候…":"正在停止并释放显存…",start?"Loading model…":"Stopping and releasing VRAM…");Build();
   try{await Task.Run(()=>Backend.Run(start?"start":"stop",target));var check=await Task.Run(()=>Backend.Probe());service=check;
    var entry=models.FirstOrDefault(x=>x.Id==target);bool okay=start?check.Ready&&entry!=null&&check.RunningAlias==entry.Alias:!check.Any;
    if(okay)SetNotice(start?"模型已就绪":"服务已停止",start?"Model ready":"Services stopped");else if(start&&check.Loading){}else SetNotice("状态未就绪，请查看日志","State not ready; check logs");
   }catch(Exception ex){SetNotice("操作未完成，请查看日志","Operation failed; check logs");LogError(ex);}
   finally{busy=false;generation++;Build();} await Poll();
  }
  static void LogError(Exception ex){try{File.AppendAllText(SS.Core.AppPaths.Log("console-error.log"),DateTime.Now.ToString("s")+" "+ex+"\r\n");}catch{}}
  public async Task Capture(string path){for(int i=0;i<150&&polling;i++)await Task.Delay(100);await Poll();UpdateLayout();await Task.Delay(Prefs.ReducedMotion?100:950);UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth),(int)Math.Ceiling(ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(this);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);}
 }
 public static class Entry {
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string c,string title);
  [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr h,int cmd);
  [STAThread]public static int Main(string[] args){
   try{SS.Core.AppPaths.Initialize();if(args.Length==2&&args[0]=="--telemetry"){File.WriteAllText(args[1],new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new InferenceMonitor.Collector().Poll()));return 0;}if(args.Length==2&&args[0]=="--import"){SS.Core.ModelDiscovery.AddFiles(new[]{args[1]});return 0;}if(args.Length>=2&&args[0]=="--service"){if(args.Length==3&&args[1]=="start")SS.Core.NativeHost.Start(args[2]);else if(args.Length==3&&args[1]=="restart")SS.Core.NativeHost.Restart(args[2],SS.Core.NativeHost.RunningAlias());else if(args.Length==2&&args[1]=="stop")SS.Core.NativeHost.Stop();else return 2;return 0;}if(args.Length>1&&args[0]=="--host"){SS.Core.NativeHost.MainHost(args[1]);return 0;}bool preview=args.Length>1&&args[0]=="--preview",created;
    using(var mutex=new Mutex(true,preview?"Local\\SSLocalModelManagerPreview":"Local\\SSLocalModelManager",out created)){
     if(!created){var h=FindWindow(null,"SS本地模型管理器");if(h==IntPtr.Zero)h=FindWindow(null,"SS Local Model Manager");if(h==IntPtr.Zero)h=FindWindow(null,"本地模型启动器");if(h!=IntPtr.Zero){ShowWindow(h,9);SetForegroundWindow(h);}return 0;}
     var app=new Application();var window=new ConsoleWindow();
     if(args.Length>0&&args[0]=="--selftest"){Console.WriteLine("PASS version="+ReleaseInfo.Version+" models=dynamic metrics=real-only");window.Close();return 0;}
     if((args.Length>0&&args[0]=="--monitor")||Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]).Contains("监控"))window.Navigate(2);
     if(preview)window.Loaded+=async(s,e)=>{try{if(args.Length>2)window.Navigate(int.Parse(args[2]));if(args.Length>3){window.Prefs.Light=args[3]=="light";window.Navigate(window.CurrentPage);}if(args.Length>4){window.Prefs.Language=args[4];window.Navigate(window.CurrentPage);}if(args.Length>6){window.Width=double.Parse(args[5]);window.Height=double.Parse(args[6]);}if(args.Length>7)window.MonitorSection(int.Parse(args[7]));await window.Poll();await window.Capture(args[1]);}finally{window.Close();}};
     app.Run(window);return 0;
    }
   }catch(Exception e){try{File.WriteAllText(SS.Core.AppPaths.Log("console-fatal.log"),e.ToString());}catch{}return 1;}
  }
 }
}







