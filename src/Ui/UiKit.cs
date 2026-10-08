using W=System.Windows;
using C=System.Windows.Controls;
using M=System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Media.Animation;

namespace SdrCapture.Ui;

static class Kit
{
    public static M.Brush Brush(string color)=>(M.Brush)new M.BrushConverter().ConvertFromString(color)!;
    public static C.TextBlock Text(string value,double size=13,string color="#F8F7FB")
    {
        var text=new C.TextBlock{FontSize=size,Foreground=Brush(color),TextWrapping=W.TextWrapping.Wrap};
        text.SetBinding(C.TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(UiStrings.Language)){Source=UiStrings.Shared,Converter=new UiTextConverter(),ConverterParameter=value});return text;
    }
    public static C.Button Button(string label,Action action,bool primary=false)
    {
        var button=new C.Button{Content=Text(label),Tag=label};if(primary)button.SetResourceReference(W.FrameworkElement.StyleProperty,"Primary");
        button.Click+=(_,_)=>action();button.MouseEnter+=(_,_)=>Pulse(button);return button;
    }
    public static C.Button AsyncButton(string label,Func<Task> action)
    {
        var button=new C.Button{Content=Text(label),Tag=label};button.Click+=async(_,_)=>{button.IsEnabled=false;try{await action();}catch(Exception e){Log.Write("UI action: "+e);if(W.Window.GetWindow(button) is {} owner&&!owner.Dispatcher.HasShutdownStarted)Notify(owner,e.Message);}finally{button.IsEnabled=true;}};return button;
    }
    public static C.Button IconButton(string label,string geometry,Action action,string style="IconButton")
    {
        var button=Button(label,action);button.SetResourceReference(W.FrameworkElement.StyleProperty,style);
        var icon=new System.Windows.Shapes.Path{Data=M.Geometry.Parse(geometry),StrokeThickness=1.5,StrokeStartLineCap=M.PenLineCap.Round,StrokeEndLineCap=M.PenLineCap.Round,Width=12,Height=12,Stretch=M.Stretch.Uniform};
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding(nameof(C.Button.Foreground)){Source=button});button.Content=icon;button.ToolTip=Text(label,12);
        button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,new System.Windows.Data.Binding(nameof(UiStrings.Language)){Source=UiStrings.Shared,Converter=new UiTextConverter(),ConverterParameter=label});return button;
    }
    public static C.StackPanel Stack(params W.UIElement[] items){var stack=new C.StackPanel();foreach(var item in items)stack.Children.Add(item);return stack;}
    public static C.StackPanel Actions(params W.UIElement[] items)
    {var stack=new C.StackPanel{Orientation=C.Orientation.Horizontal};foreach(var item in items){if(item is W.FrameworkElement f)f.Margin=new(0,0,8,0);stack.Children.Add(item);}return stack;}
    public static C.Border Card(W.UIElement content)
    {var card=new C.Border{BorderBrush=Brush("#24FFFFFF"),BorderThickness=new(1),CornerRadius=new(12),Padding=new(20),Margin=new(0,0,0,14),Child=content};card.SetResourceReference(C.Border.BackgroundProperty,"Card");return card;}
    public static void Pulse(W.UIElement element)
    {
        if(!W.SystemParameters.ClientAreaAnimation)return;
        element.BeginAnimation(W.UIElement.OpacityProperty,new DoubleAnimation(.78,1,TimeSpan.FromMilliseconds(150)){FillBehavior=FillBehavior.Stop});
    }
    public static void Enter(W.UIElement element)
    {
        ResetTransition(element);if(!W.SystemParameters.ClientAreaAnimation)return;
        var motion=new M.TranslateTransform();element.RenderTransform=motion;
        motion.BeginAnimation(M.TranslateTransform.XProperty,new DoubleAnimation(16,0,TimeSpan.FromMilliseconds(220)){EasingFunction=new QuarticEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);
        element.BeginAnimation(W.UIElement.OpacityProperty,new DoubleAnimation(.08,1,TimeSpan.FromMilliseconds(200)){EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);
    }
    public static void ResetTransition(W.UIElement element)
    {element.BeginAnimation(W.UIElement.OpacityProperty,null);element.Opacity=1;element.RenderTransform=M.Transform.Identity;}
    public static void Navigation(C.Button button,bool selected,bool animate)
    {
        void Set(W.DependencyProperty property,string color)
        {
            var target=((M.SolidColorBrush)Brush(color)).Clone();var previous=(button.GetValue(property) as M.SolidColorBrush)?.Color??M.Colors.Transparent;button.SetValue(property,target);
            if(animate&&W.SystemParameters.ClientAreaAnimation&&previous!=target.Color)target.BeginAnimation(M.SolidColorBrush.ColorProperty,new ColorAnimation(previous,target.Color,TimeSpan.FromMilliseconds(170)){FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);
        }
        Set(C.Control.BackgroundProperty,selected?"#302039":"Transparent");Set(C.Control.BorderBrushProperty,selected?"#7C3C83":"Transparent");
    }
    public static void Notify(W.Window owner,string message,string title="DeskGlide")=>W.MessageBox.Show(owner,UiStrings.T(message),UiStrings.T(title),W.MessageBoxButton.OK,W.MessageBoxImage.Information);
    public static W.DataTemplate ChoiceTemplate()
    {
        var factory=new W.FrameworkElementFactory(typeof(C.TextBlock));
        var binding=new System.Windows.Data.MultiBinding{Converter=new UiChoiceConverter()};binding.Bindings.Add(new System.Windows.Data.Binding("Label"));binding.Bindings.Add(new System.Windows.Data.Binding(nameof(UiStrings.Language)){Source=UiStrings.Shared});
        factory.SetBinding(C.TextBlock.TextProperty,binding);return new W.DataTemplate{VisualTree=factory};
    }
}

class ShellWindow:W.Window
{
    readonly System.Windows.Shell.WindowChrome chrome=new(){CaptionHeight=0,ResizeBorderThickness=new(0),GlassFrameThickness=new(0),CornerRadius=new(0),UseAeroCaptionButtons=false};
    C.Border? header;
    public bool IsClosed {get;private set;}
    public ShellWindow(string title,double width,double height)
    {
        Resources.MergedDictionaries.Add(new W.ResourceDictionary{Source=new Uri("/ScreenCapture;component/Ui/Theme.xaml",UriKind.Relative)});
        Style=(W.Style)FindResource(typeof(W.Window));Title="DeskGlide · "+UiStrings.T(title);Width=width;Height=height;WindowStartupLocation=W.WindowStartupLocation.CenterScreen;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this,chrome);
        System.ComponentModel.PropertyChangedEventHandler languageChanged=(_,_)=>Title="DeskGlide · "+UiStrings.T(title);UiStrings.Shared.PropertyChanged+=languageChanged;
        Icon=System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/ScreenCapture;component/Assets/DeskGlide.ico"));
        Closed+=(_,_)=>{IsClosed=true;UiStrings.Shared.PropertyChanged-=languageChanged;};
        SourceInitialized+=(_,_)=>
        {
            var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;int dark=1;DwmSetWindowAttribute(handle,20,ref dark,4);
            int rounded=2;DwmSetWindowAttribute(handle,33,ref rounded,4);int noSystemBorder=-2;DwmSetWindowAttribute(handle,34,ref noSystemBorder,4);
            // Native Acrylic uses the compositor; no layered window, input overlay or blur loop.
            int acrylic=3;if(DwmSetWindowAttribute(handle,38,ref acrylic,4)==0)
            {
                if(System.Windows.Interop.HwndSource.FromHwnd(handle)?.CompositionTarget is {} target)target.BackgroundColor=M.Colors.Transparent;
                var margins=new Margins(-1,-1,-1,-1);DwmExtendFrameIntoClientArea(handle,ref margins);
            }
            else Background=Kit.Brush("#101018");
        };
        Loaded+=(_,_)=>
        {
            var source=W.PresentationSource.FromVisual(this);double sx=source?.CompositionTarget?.TransformToDevice.M11??1,sy=source?.CompositionTarget?.TransformToDevice.M22??1;
            var bounds=Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
            Width=Math.Min(Width,(bounds.Width-24)/sx);Height=Math.Min(Height,(bounds.Height-24)/sy);
            Left=Math.Clamp(Left,bounds.Left/sx,Math.Max(bounds.Left/sx,(bounds.Right-Width*sx)/sx));Top=Math.Clamp(Top,bounds.Top/sy,Math.Max(bounds.Top/sy,(bounds.Bottom-Height*sy)/sy));
        };
    }
    public void Reveal()
    {
        if(IsClosed)return;
        if(!IsVisible){if(W.Application.Current==null)System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);Show();}
        if(WindowState==W.WindowState.Minimized)WindowState=W.WindowState.Normal;
        Activate();SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();if(Template.FindName("WindowFrame",this) is C.Border frame){frame.SizeChanged+=(_,_)=>ClipFrame(frame);ClipFrame(frame);}
    }
    static void ClipFrame(C.Border frame)
    {if(frame.ActualWidth>0&&frame.ActualHeight>0)frame.Clip=new M.RectangleGeometry(new W.Rect(0,0,frame.ActualWidth,frame.ActualHeight),frame.CornerRadius.TopLeft,frame.CornerRadius.TopLeft);}
    protected C.Border Header(string title,string subtitle,bool compact=false)
    {
        var heading=Kit.Text(title,compact?17:25);heading.FontWeight=W.FontWeights.SemiBold;
        var copy=Kit.Stack(heading);copy.VerticalAlignment=W.VerticalAlignment.Center;
        if(subtitle.Length>0){var caption=Kit.Text(subtitle,12,"#B0A9BC");caption.Margin=new(0,6,0,0);copy.Children.Add(caption);}
        var grid=new C.Grid();grid.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
        var mark=new C.Image{Width=compact?28:42,Height=compact?28:42,Margin=new(0,0,14,0),Source=System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/ScreenCapture;component/Assets/DeskGlide.png")),ToolTip="DeskGlide"};grid.Children.Add(mark);C.Grid.SetColumn(copy,1);grid.Children.Add(copy);
        var controls=new C.StackPanel{Orientation=C.Orientation.Horizontal,VerticalAlignment=W.VerticalAlignment.Top,Margin=new(20,0,0,0)};
        var minimize=Kit.IconButton("Minimize window","M0 6 H12",()=>System.Windows.SystemCommands.MinimizeWindow(this));
        minimize.Visibility=ResizeMode==W.ResizeMode.NoResize?W.Visibility.Collapsed:W.Visibility.Visible;
        var close=Kit.IconButton("Close window","M1 1 L11 11 M11 1 L1 11",Close,"CloseWindowButton");close.Margin=new(4,0,0,0);
        foreach(var button in new[]{minimize,close}){System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(button,true);controls.Children.Add(button);}
        C.Grid.SetColumn(controls,2);grid.Children.Add(controls);
        header=new C.Border{Padding=new(compact?18:26,compact?12:22,18,compact?12:20),BorderBrush=Kit.Brush("#24FFFFFF"),BorderThickness=new(0,0,0,1),Background=Kit.Brush("#A6100E18"),Child=grid};
        header.SizeChanged+=(_,_)=>{if(System.Windows.Shell.WindowChrome.GetWindowChrome(this)!=null)chrome.CaptionHeight=header.ActualHeight;};return header;
    }
    protected void FullscreenChrome(bool enabled)
    {
        if(header!=null)header.Visibility=enabled?W.Visibility.Collapsed:W.Visibility.Visible;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this,enabled?null:chrome);WindowStyle=enabled?W.WindowStyle.None:W.WindowStyle.SingleBorderWindow;
        if(Template.FindName("WindowFrame",this) is C.Border frame){frame.CornerRadius=new(enabled?0:12);frame.BorderThickness=new(enabled?0:1);ClipFrame(frame);}
        int preference=enabled?1:2;DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle,33,ref preference,4);
    }
    [StructLayout(LayoutKind.Sequential)] readonly record struct Margins(int Left,int Right,int Top,int Bottom);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
}

sealed class ShortcutBox:C.StackPanel
{
    readonly C.CheckBox ctrl=new(){Content="Ctrl"},alt=new(){Content="Alt"},shift=new(){Content="Shift"};
    readonly C.ComboBox key=new(){Width=105};
    sealed record KeyChoice(uint Id,string Label){public override string ToString()=>Label;}
    public uint Modifiers=>(uint)((ctrl.IsChecked==true?2:0)|(alt.IsChecked==true?1:0)|(shift.IsChecked==true?4:0));
    public uint Key=>((KeyChoice)key.SelectedItem).Id;
    public ShortcutBox(uint modifiers,uint code)
    {
        Orientation=C.Orientation.Horizontal;ctrl.IsChecked=(modifiers&2)!=0;alt.IsChecked=(modifiers&1)!=0;shift.IsChecked=(modifiers&4)!=0;
        key.Items.Add(new KeyChoice(0,"Off"));foreach(var k in Enumerable.Range((int)Keys.A,26).Concat(Enumerable.Range((int)Keys.D0,10)).Concat(Enumerable.Range((int)Keys.F1,12)).Append((int)Keys.Pause))key.Items.Add(new KeyChoice((uint)k,((Keys)k).ToString()));
        key.ItemTemplate=Kit.ChoiceTemplate();key.SelectedItem=key.Items.Cast<KeyChoice>().FirstOrDefault(k=>k.Id==code)??key.Items[0];
        foreach(var item in new W.FrameworkElement[]{ctrl,alt,shift,key}){item.Margin=new(0,0,12,0);Children.Add(item);}
    }
}
