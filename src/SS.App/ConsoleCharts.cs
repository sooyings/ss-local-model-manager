using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace ModelConsole {
 public static class Ink {
  public static Brush B(string s){var b=(SolidColorBrush)new BrushConverter().ConvertFromString(s);b.Freeze();return b;}
  public static void Text(DrawingContext d,string s,double x,double y,double size,Brush color,bool bold=false){
   var f=new FormattedText(s,CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface(new FontFamily("Microsoft YaHei UI"),FontStyles.Normal,bold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal),size,color,1.0);
   d.DrawText(f,new Point(x,y));
  }
 }
 public class Gauge:FrameworkElement {
  public string Label="",Unit="tok/s",Caption="",Accent="#191919",Track="#555555",Foreground="#191919";
  public double Max=100; public bool Known,Reduced;
  public static readonly DependencyProperty ValueProperty=DependencyProperty.Register("Value",typeof(double),typeof(Gauge),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
  public double Value {get{return (double)GetValue(ValueProperty);}set{SetValue(ValueProperty,value);}}
  public void Set(double? value,double max,bool reduced){
   double from=Value;BeginAnimation(ValueProperty,null);Max=Math.Max(1,max);Known=value.HasValue;Reduced=reduced;
   Value=value??0;
   if(!reduced&&Known&&Math.Abs(from-Value)>0.05)BeginAnimation(ValueProperty,new DoubleAnimation(from,Value,TimeSpan.FromMilliseconds(550)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
   InvalidateVisual();
  }
  Point P(double a,double r,Point c){double t=a*Math.PI/180;return new Point(c.X+Math.Cos(t)*r,c.Y+Math.Sin(t)*r);}
  void Arc(DrawingContext d,Point c,double r,double start,double end,Pen pen){if(end-start<=0.01)return;var g=new StreamGeometry();using(var x=g.Open()){x.BeginFigure(P(start,r,c),false,false);x.ArcTo(P(end,r,c),new Size(r,r),0,end-start>180,SweepDirection.Clockwise,true,false);}g.Freeze();d.DrawGeometry(null,pen,g);}
  protected override void OnRender(DrawingContext d){
   double w=ActualWidth,h=ActualHeight;double r=Math.Min(w*.39,(h-28)*.53);var center=new Point(w/2,r+7);var ink=Ink.B(Foreground);var accent=Ink.B(Accent);
   Arc(d,center,r,140,400,new Pen(Ink.B(Track),6){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round});
   for(int i=0;i<=20;i++){double a=140+i*13;d.DrawLine(new Pen(Ink.B(Track),i%5==0?1.4:.7),P(a,r-9,center),P(a,r-(i%5==0?14:12),center));}
   if(Known){double angle=140+260*Math.Min(1,Value/Max);Arc(d,center,r,140,angle,new Pen(accent,6){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round});d.DrawLine(new Pen(accent,1.6),P(angle,8,center),P(angle,r-17,center));d.DrawEllipse(ink,null,center,2.8,2.8);}
   string number=Known?Value.ToString(Value>=100?"0":"0.0",CultureInfo.InvariantCulture):"—";
   var f=new FormattedText(number,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),h<85?18:23,ink,1.0);f.SetFontWeight(FontWeights.Bold);d.DrawText(f,new Point((w-f.Width)/2,center.Y+(h<85?3:9)));
   var caption=new FormattedText(Label+" · "+Unit,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),h<85?9:10,ink,1.0);d.DrawText(caption,new Point((w-caption.Width)/2,h-13));
  }
 }
 public class Meter:FrameworkElement {
  public double? Percent; public string Accent="#202020",Track="#AAAAAA"; public bool Reduced;
  public static readonly DependencyProperty LevelProperty=DependencyProperty.Register("Level",typeof(double),typeof(Meter),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
  public double Level{get{return (double)GetValue(LevelProperty);}set{SetValue(LevelProperty,value);}}
  public void Set(double? p,bool reduced){double from=Level;BeginAnimation(LevelProperty,null);Percent=p;Level=p??0;Reduced=reduced;
   if(p.HasValue&&!reduced)BeginAnimation(LevelProperty,new DoubleAnimation(from,Level,TimeSpan.FromMilliseconds(500)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
   InvalidateVisual();}
  protected override void OnRender(DrawingContext d){double gap=3,w=(ActualWidth-gap*23)/24;for(int i=0;i<24;i++){double opacity=!Percent.HasValue?.25:(i<Level/100*24?1:.16);d.PushOpacity(opacity);d.DrawRoundedRectangle(Ink.B(Percent.HasValue?Accent:Track),null,new Rect(i*(w+gap),0,Math.Max(1,w),ActualHeight),1.8,1.8);d.Pop();}}
 }
 public class Plot:FrameworkElement {
  public List<double?> Values=new List<double?>();public string Accent="#202020",Track="#555555";public bool Bars;public double Floor=1;
  public static readonly DependencyProperty RevealProperty=DependencyProperty.Register("Reveal",typeof(double),typeof(Plot),new FrameworkPropertyMetadata(1.0,FrameworkPropertyMetadataOptions.AffectsRender));
  public double Reveal{get{return (double)GetValue(RevealProperty);}set{SetValue(RevealProperty,value);}}
  public void Set(List<double?> values,bool animate){Values=new List<double?>(values);BeginAnimation(RevealProperty,null);Reveal=1;if(animate)BeginAnimation(RevealProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(650)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});InvalidateVisual();}
  protected override void OnRender(DrawingContext d){
   double w=ActualWidth,h=ActualHeight;d.PushClip(new RectangleGeometry(new Rect(0,0,w*Reveal,h)));
   var track=Ink.B(Track);for(int i=1;i<=3;i++)d.DrawLine(new Pen(track,.4),new Point(0,h*i/4),new Point(w,h*i/4));
   if(Values.Count<2){Ink.Text(d,Values.Count==0?"—":"·",w/2-3,h/2-8,12,track);d.Pop();return;}
   double max=Floor;foreach(var n in Values)if(n.HasValue)max=Math.Max(max,n.Value);max*=1.15;
   Point? last=null;for(int i=0;i<Values.Count;i++){if(!Values[i].HasValue){last=null;continue;}double x=w*i/(Values.Count-1.0),y=h-3-(h-6)*Values[i].Value/max;
    if(Bars){double bw=Math.Max(1,w/Values.Count-3);d.DrawRoundedRectangle(Ink.B(Accent),null,new Rect(x-bw/2,y,bw,Math.Max(1,h-y)),1,1);}
    else {var p=new Point(x,y);if(last.HasValue)d.DrawLine(new Pen(Ink.B(Accent),1.8){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round},last.Value,p);last=p;}
   }d.Pop();
  }
 }
}


