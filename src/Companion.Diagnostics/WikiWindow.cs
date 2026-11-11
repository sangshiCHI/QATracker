using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Companion.Diagnostics;

internal static class WikiUi
{
    public static SolidColorBrush Brush(string color)=>new((Color)ColorConverter.ConvertFromString(color));
    static readonly Lazy<ControlTemplate> ButtonTemplate=new(()=>(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Border Name="rim" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="rim" Property="Background" Value="#98703a"/><Setter TargetName="rim" Property="BorderBrush" Value="#ffe49a"/></Trigger>
          <Trigger Property="IsPressed" Value="True"><Setter TargetName="rim" Property="Background" Value="#49301c"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """));
    public static Button Button(string text)=>new(){Content=text,Background=Brush("#705027"),Foreground=Brush("#fff1ce"),BorderBrush=Brush("#c69b47"),Padding=new Thickness(9,4,9,4),Cursor=Cursors.Hand,Template=ButtonTemplate.Value};
    public static Image Image(ImageSource? source,double size)
    {
        var image=new Image{Source=source,Width=size,Height=size,Stretch=Stretch.Uniform};
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor);return image;
    }
    public static void Frame(Window window,string title,UIElement content,Action? back=null)
    {
        window.Icon=AppIcon.Image;
        // HWND transparency is fixed by the owning Window constructor; switching content must not set it again.
        window.Background=Brushes.Transparent;
        window.Foreground=Brush("#f5e7cd");window.FontFamily=new FontFamily("Microsoft YaHei UI");window.ShowInTaskbar=false;window.Topmost=true;
        var root=new DockPanel{Margin=new Thickness(9)};
        var bar=new DockPanel{Margin=new Thickness(2,0,2,8),LastChildFill=true};
        var close=Button("×");close.FontSize=17;close.Padding=new Thickness(8,0,8,0);close.ToolTip="关闭";close.Click+=(_,_)=>window.Close();
        DockPanel.SetDock(close,Dock.Right);bar.Children.Add(close);
        if(back is not null){var previous=Button("‹ 搜索");previous.Margin=new Thickness(0,0,8,0);previous.Click+=(_,_)=>back();DockPanel.SetDock(previous,Dock.Left);bar.Children.Add(previous);}
        var label=new TextBlock{Text=title,Foreground=Brush("#daba76"),FontSize=13,VerticalAlignment=VerticalAlignment.Center};
        bar.Children.Add(label);bar.MouseLeftButtonDown+=(_,e)=>{if(e.ChangedButton==MouseButton.Left)try{window.DragMove();}catch(InvalidOperationException){}};
        DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);root.Children.Add(content);
        window.Content=new Border{BorderBrush=Brush("#c39743"),BorderThickness=new Thickness(2),CornerRadius=new CornerRadius(9),
            Background=new LinearGradientBrush(Color.FromRgb(66,44,26),Color.FromRgb(34,23,16),90),Child=root};
    }
    public static void SaveOwnWindow(Window window,string output,string file)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap(Math.Max(1,(int)window.ActualWidth),Math.Max(1,(int)window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory(output);
        using var stream=File.Create(Path.Combine(output,file));encoder.Save(stream);
    }
}

internal sealed class WikiWindow:Window
{
    const double SearchWidth=300;
    readonly TextBox query=new(){FontSize=14,Padding=new Thickness(4,6,4,6),MaxLength=100,Background=Brushes.Transparent,Foreground=WikiUi.Brush("#f0e2c8"),CaretBrush=WikiUi.Brush("#f0e2c8"),BorderThickness=new Thickness(0)};
    readonly ListBox results=new(){Background=Brushes.Transparent,Foreground=Brushes.Ivory,BorderThickness=new Thickness(0),Padding=new Thickness(0)};
    readonly TextBlock status=new(){Text="没有结果。试试更短的词。",Foreground=WikiUi.Brush("#eedab3"),FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(9,9,9,9)};
    readonly TextBlock placeholder=new(){Text="搜索百科…",Foreground=WikiUi.Brush("#d0bc96"),FontSize=14,Margin=new Thickness(4,0,0,0),IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center};
    readonly StackPanel searchRoot=new(){Background=Brushes.Transparent};
    readonly Border dropdown=new(){CornerRadius=new CornerRadius(6),BorderBrush=WikiUi.Brush("#bd9545"),BorderThickness=new Thickness(2),Margin=new Thickness(0,5,0,0)};
    readonly DispatcherTimer debounce=new(){Interval=TimeSpan.FromMilliseconds(120)};
    readonly DispatcherTimer outsideWatch=new(){Interval=TimeSpan.FromMilliseconds(25)};
    readonly bool autoDismiss;
    Window? anchorWindow;double preferredHeight=48;int previousButtons;bool dismissing;
    internal Func<Rect[]>? AdditionalExclusions{get;set;}
    internal static long LastOutsideDismissalTick{get;private set;}
    WikiSearch? data;WikiDetailView? detailView;bool closed;bool showingDetail;
    public WikiWindow(bool autoDismiss=true)
    {
        this.autoDismiss=autoDismiss;
        Title="QATK 0.1 · 中文 Wiki";Icon=AppIcon.Image;Width=SearchWidth;Height=48;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;Topmost=true;
        FontFamily=new FontFamily("Microsoft YaHei UI");
        var field=new Grid();field.ColumnDefinitions.Add(new(){Width=new GridLength(27)});field.ColumnDefinitions.Add(new());field.ColumnDefinitions.Add(new(){Width=new GridLength(26)});
        field.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M 8,2 A 6,6 0 1 0 8,14 A 6,6 0 1 0 8,2 M 12,12 L 18,18"),Stroke=WikiUi.Brush("#ddc590"),StrokeThickness=1.5,Width=17,Height=17,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Stretch=Stretch.Uniform});
        var input=new Grid();input.Children.Add(query);input.Children.Add(placeholder);Grid.SetColumn(input,1);field.Children.Add(input);
        var close=WikiUi.Button("×");close.Width=24;close.Height=24;close.Padding=new Thickness(0);close.Background=Brushes.Transparent;close.BorderBrush=Brushes.Transparent;close.Foreground=WikiUi.Brush("#ddc590");close.ToolTip="关闭";close.Click+=(_,_)=>Close();Grid.SetColumn(close,2);field.Children.Add(close);
        searchRoot.Children.Add(new Border{CornerRadius=new CornerRadius(6),BorderBrush=WikiUi.Brush("#aaaf8844"),BorderThickness=new Thickness(1),Padding=new Thickness(5,1,5,1),Background=Paper(),Child=field});
        searchRoot.Children.Add(dropdown);Content=searchRoot;
        System.Windows.Automation.AutomationProperties.SetName(query,"百科关键词");
        results.ItemTemplate=(DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Margin="8,4"><Grid.ColumnDefinitions><ColumnDefinition Width="36"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <Image Source="{Binding Icon}" Width="28" Height="28" Stretch="Uniform" RenderOptions.BitmapScalingMode="NearestNeighbor"/>
                <TextBlock Grid.Column="1" Text="{Binding Title}" Foreground="#fff1d2" FontWeight="SemiBold" FontSize="14" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                <TextBlock Grid.Column="2" Text="{Binding Category}" Foreground="#d6bd8c" FontSize="11" Margin="8,0,0,0" VerticalAlignment="Center"/>
              </Grid>
            </DataTemplate>
            """);
        results.ItemContainerStyle=(Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Margin" Value="2,1"/><Setter Property="Cursor" Value="Hand"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                <Border Name="tile" Background="#654622" BorderBrush="#af8442" BorderThickness="1" CornerRadius="4"><ContentPresenter/></Border>
                <ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="tile" Property="BorderBrush" Value="#ffdc81"/><Setter TargetName="tile" Property="Background" Value="#8b6230"/></Trigger>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="tile" Property="Background" Value="#98703b"/><Setter TargetName="tile" Property="BorderBrush" Value="#f4d17a"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        query.TextChanged+=(_,_)=>{placeholder.Visibility=query.Text.Length==0?Visibility.Visible:Visibility.Collapsed;debounce.Stop();debounce.Start();};
        debounce.Tick+=(_,_)=>{debounce.Stop();Search();};
        query.PreviewKeyDown+=(_,e)=>
        {
            if((e.Key is Key.Down or Key.Up)&&results.Items.Count>0)
            {var delta=e.Key==Key.Down?1:-1;results.SelectedIndex=Math.Clamp(results.SelectedIndex+delta,0,results.Items.Count-1);results.ScrollIntoView(results.SelectedItem);e.Handled=true;}
            if(e.Key==Key.Enter){OpenSelected();e.Handled=true;}
        };
        results.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter&&results.SelectedItem is WikiEntry entry){Open(entry);e.Handled=true;}};
        results.MouseLeftButtonUp+=(_,e)=>{if(e.OriginalSource is DependencyObject element&&ItemsControl.ContainerFromElement(results,element) is ListBoxItem item&&item.Content is WikiEntry entry)Open(entry);};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape&&!showingDetail){Close();e.Handled=true;}};
        Deactivated+=(_,_)=>{if(autoDismiss&&!showingDetail)DismissOutside();};
        Closing+=(_,_)=>dismissing=true;
        Closed+=(_,_)=>{closed=true;debounce.Stop();outsideWatch.Stop();anchorWindow=null;};
        outsideWatch.Tick+=(_,_)=>
        {
            if(anchorWindow is not null&&!anchorWindow.IsVisible){DismissOutside();return;}
            PositionBesideAnchor();
            var pointer=Native.PopupPointer();CheckOutsidePointer(pointer.X,pointer.Y,pointer.Buttons);
        };
        Loaded+=(_,_)=>{if(autoDismiss){previousButtons=Native.PopupPointer().Buttons;outsideWatch.Start();}};
        Loaded+=async (_,_)=>{try{var loaded=await Task.Run(()=>WikiSearch.Local.Value);if(closed)return;data=loaded;Search();query.Focus();}
            catch(Exception e)when(e is IOException or InvalidDataException or System.Text.Json.JsonException){if(!closed){status.Text="本地资料暂不可用";dropdown.Child=status;dropdown.Background=Paper();dropdown.Visibility=Visibility.Visible;Height=94;}}};
    }
    static Brush Paper()=>new LinearGradientBrush(Color.FromArgb(235,44,30,20),Color.FromArgb(225,34,23,16),90);
    internal bool PlaceNear(Window anchor)
    {
        anchorWindow=anchor;WindowStartupLocation=WindowStartupLocation.Manual;return PositionBesideAnchor();
    }
    internal string PreferredSide{get;set;}="top";
    internal Func<string>? PreferredSideProvider{get;set;}
    internal void RefreshPlacement(){if(anchorWindow is not null)PositionBesideAnchor();}
    bool PositionBesideAnchor()
    {
        if(closed||dismissing||anchorWindow is null||!anchorWindow.IsVisible)return false;
        var start=anchorWindow.PointToScreen(new Point(0,0));
        var end=anchorWindow.PointToScreen(new Point(anchorWindow.ActualWidth,anchorWindow.ActualHeight));
        var dpi=VisualTreeHelper.GetDpi(anchorWindow);
        var screen=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)start.X,(int)start.Y)).WorkingArea;
        var placement=WikiPopupPlacement.Choose(new Rect(start,end),new Rect(screen.X,screen.Y,screen.Width,screen.Height),
            new Size((showingDetail?780:SearchWidth)*dpi.DpiScaleX,preferredHeight*dpi.DpiScaleY),8*dpi.DpiScaleX,160*dpi.DpiScaleX,36*dpi.DpiScaleY,AdditionalExclusions?.Invoke(),PreferredSideProvider?.Invoke()??PreferredSide);
        if(placement is not Rect rect){if(IsVisible)DismissOutside();return false;}
        var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if(handle==0){Left=rect.X/dpi.DpiScaleX;Top=rect.Y/dpi.DpiScaleY;Width=rect.Width/dpi.DpiScaleX;Height=rect.Height/dpi.DpiScaleY;}
        else if(IsVisible&&!Native.OverlayIsAt(handle,(int)rect.X,(int)rect.Y,(int)rect.Width,(int)rect.Height))
            try{Native.PositionOverlay(handle,(int)rect.X,(int)rect.Y,(int)rect.Width,(int)rect.Height);}
            catch(System.ComponentModel.Win32Exception){DismissOutside();return false;}
        return true;
    }
    void DismissOutside()
    {
        if(closed||dismissing||showingDetail)return;LastOutsideDismissalTick=Environment.TickCount64;dismissing=true;Close();
    }
    internal static bool NewOutsidePress(Rect bounds,Point pointer,int buttons,ref int previous)
    {
        var pressed=(buttons&~previous)!=0;previous=buttons;return pressed&&!bounds.Contains(pointer);
    }
    void CheckOutsidePointer(int x,int y,int buttons)
    {
        if(closed||dismissing||showingDetail){previousButtons=buttons;return;}
        var first=PointToScreen(new Point(0,0));var last=PointToScreen(new Point(ActualWidth,ActualHeight));
        if(NewOutsidePress(new Rect(first,last),new Point(x,y),buttons,ref previousButtons))DismissOutside();
    }
    void ClampToScreen(System.Drawing.Rectangle area,DpiScale dpi)
    {
        Left=Math.Clamp(Left,area.Left/dpi.DpiScaleX,Math.Max(area.Left/dpi.DpiScaleX,area.Right/dpi.DpiScaleX-Width));
        Top=Math.Clamp(Top,area.Top/dpi.DpiScaleY,Math.Max(area.Top/dpi.DpiScaleY,area.Bottom/dpi.DpiScaleY-Height));
    }
    void ClampToScreen()
    {
        preferredHeight=Height;
        if(anchorWindow is not null){PositionBesideAnchor();return;}
        var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if(handle!=0)ClampToScreen(System.Windows.Forms.Screen.FromHandle(handle).WorkingArea,VisualTreeHelper.GetDpi(this));
    }
    void Search()
    {
        if(data is null||showingDetail)return;
        var found=data.Find(query.Text);results.ItemsSource=found;
        if(query.Text.Trim().Length<2){dropdown.Visibility=Visibility.Collapsed;Height=48;}
        else if(found.Count==0){dropdown.Child=status;dropdown.Background=Paper();dropdown.Visibility=Visibility.Visible;Height=94;}
        else{dropdown.Child=results;dropdown.Background=WikiUi.Brush("#4f361f");dropdown.Visibility=Visibility.Visible;results.MaxHeight=6*42;Height=52+Math.Min(found.Count,6)*42+8;results.SelectedIndex=0;}
        ClampToScreen();
    }
    internal void ShowSearch()
    {
        showingDetail=false;detailView=null;Width=SearchWidth;Content=searchRoot;Search();query.Focus();if(autoDismiss&&IsLoaded){previousButtons=Native.PopupPointer().Buttons;outsideWatch.Start();}
    }
    void Open(WikiEntry entry)
    {
        if(data is null)return;debounce.Stop();showingDetail=true;outsideWatch.Stop();
        detailView=new WikiDetailView(data,entry);
        Width=780;Height=detailView.PreferredHeight;WikiUi.Frame(this,"",detailView,ShowSearch);ClampToScreen();
    }
    void OpenSelected()
    {
        if(debounce.IsEnabled){debounce.Stop();Search();}
        if(results.Items.Count>0)Open((WikiEntry)results.Items[Math.Max(0,results.SelectedIndex)]);
    }
    internal async Task VerifyUi(string output,Application app)
    {
        try
        {
            data=await Task.Run(()=>WikiSearch.Local.Value);query.Text="QATK_不存在的百科条目_6947623";debounce.Stop();Search();
            var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var checks=new Dictionary<string,bool>{{"unsupported query is empty, no article fallback",results.Items.Count==0},{"empty state exactly simple",status.Text=="没有结果。试试更短的词。"},{"transparent search-only root",Background==Brushes.Transparent&&Content==searchRoot&&Height<=100}};
            using(var trayMenu=new System.Windows.Forms.ContextMenuStrip())
            {
                var invoked=new List<string>();CompanionShell.FillTrayMenu(trayMenu,invoked.Add,false);
                checks["tray retains commands and adds requested About before Exit"]=trayMenu.Items.Count==6&&trayMenu.Items.Cast<System.Windows.Forms.ToolStripMenuItem>().Select(i=>i.Tag as string).SequenceEqual(new[]{"show","afk","settings","update","about","exit"});
                foreach(System.Windows.Forms.ToolStripMenuItem item in trayMenu.Items)item.PerformClick();
                checks["all retained tray commands dispatch correctly"]=invoked.SequenceEqual(new[]{"show","afk","settings","update","about","exit"});
                CompanionShell.FillTrayMenu(trayMenu,invoked.Add,true);checks["tray AFK command follows current mode"]=trayMenu.Items[1].Text=="恢复游戏";
                Program.Save(output,"tray-menu.json",trayMenu.Items.Cast<System.Windows.Forms.ToolStripMenuItem>().Select(i=>new{Text=i.Text,Action=i.Tag}).ToArray());
            }
            var aboutChecks=0;var about=CompanionShell.NewAboutWindow(()=>aboutChecks++);about.Show();about.UpdateLayout();
            checks["About shows actual assembly version and game style"]=Descendants(about).OfType<TextBlock>().Any(t=>t.Text=="当前版本：v"+SoftwareUpdate.DisplayVersion)&&about.Topmost&&about.WindowStyle==WindowStyle.None;
            Descendants(about).OfType<Button>().Single(b=>b.Tag as string=="about-update").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));checks["About reuses check update action"]=aboutChecks==1;WikiUi.SaveOwnWindow(about,output,"about-local-candidate.png");about.Close();
            var settingsAboutCount=0;var aboutSettings=CompanionShell.NewSettingsWindow();CompanionShell.BuildSettings(aboutSettings,new(),()=>{},()=>{},_=>{},showAbout:()=>settingsAboutCount++);aboutSettings.Show();
            var aboutEntry=Descendants(aboutSettings).OfType<Button>().Single(b=>b.Tag as string=="settings-about");aboutEntry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));checks["settings About entry displays version and opens action"]=settingsAboutCount==1&&aboutEntry.Content as string=="关于 · v"+SoftwareUpdate.DisplayVersion;aboutSettings.Close();
            var privateSettingsPath=Path.Combine(CompanionSettings.DataDirectory,"settings.json");var privateSettingsHash=File.Exists(privateSettingsPath)?SoftwareUpdate.Hash(privateSettingsPath):null;
            var isolatedSettings=CompanionSettings.Load(Path.Combine(output,"isolated-settings"));checks["isolated settings start with click-through default instead of player preferences"]=isolatedSettings.ClickThrough;
            isolatedSettings.ClickThrough=false;isolatedSettings.Save();var isolatedReload=CompanionSettings.Load(Path.Combine(output,"isolated-settings"));
            checks["isolated settings save and reload without changing player profile"]=!isolatedReload.ClickThrough&&(File.Exists(privateSettingsPath)?SoftwareUpdate.Hash(privateSettingsPath):null)==privateSettingsHash;
            using var faceStream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("afk.face-knight.png")!;
            using var faceBitmap=new System.Drawing.Bitmap(faceStream);
            bool MatchesWarrior(System.Drawing.Icon icon)
            {
                using var actual=icon.ToBitmap();using var expected=new System.Drawing.Bitmap(actual.Width,actual.Height);
                using(var graphics=System.Drawing.Graphics.FromImage(expected)){graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;graphics.DrawImage(faceBitmap,0,0,actual.Width,actual.Height);}
                long delta=0;for(var y=0;y<actual.Height;y++)for(var x=0;x<actual.Width;x++){var a=actual.GetPixel(x,y);var b=expected.GetPixel(x,y);delta+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)+Math.Abs(a.A-b.A);}
                return delta/(double)(actual.Width*actual.Height*4)<20;
            }
            using(var trayIcon=AppIcon.CreateTrayIcon()){checks["tray icon matches current warrior artwork"]=MatchesWarrior(trayIcon);using var bitmap=trayIcon.ToBitmap();bitmap.Save(Path.Combine(output,"warrior-tray-icon.png"),System.Drawing.Imaging.ImageFormat.Png);}
            using(var exeIcon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)){checks["published EXE has current warrior icon"]=exeIcon is not null&&MatchesWarrior(exeIcon);using var bitmap=exeIcon?.ToBitmap();bitmap?.Save(Path.Combine(output,"warrior-exe-icon.png"),System.Drawing.Imaging.ImageFormat.Png);}
            checks["generic system icon cannot pass warrior artwork check"]=!MatchesWarrior(System.Drawing.SystemIcons.Application);
            checks["window icon is reusable frozen warrior image"]=AppIcon.Image.IsFrozen&&AppIcon.Image.PixelWidth==128;
            checks["search width fits inside statistics panel"]=Width==SearchWidth&&Width<=320;
            checks["search background readable with slight transparency and undimmed text"]=((LinearGradientBrush)((Border)searchRoot.Children[0]).Background).GradientStops.All(s=>s.Color.A>=220&&s.Color.A<255)&&query.Opacity==1&&Opacity==1;
            WikiUi.SaveOwnWindow(this,output,"wiki-no-results.png");
            query.Text="等级差";debounce.Stop();Search();
            checks["section topic is the first direct search result"]=results.Items.Count>0&&((WikiEntry)results.Items[0]).Path=="/mechanics/combat#level";
            WikiUi.SaveOwnWindow(this,output,"wiki-level-search.png");
            Open(data.Find("等级差").First(e=>e.Path=="/mechanics/combat#level"));
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            checks["section opens its own local body"]=detailView!.EntryPath=="/mechanics/combat#level"&&detailView.BodyText.Contains("×0.87");
            checks["level difference table renders all five columns"]=Descendants(detailView!).OfType<Grid>().Any(g=>g.ColumnDefinitions.Count==5&&g.RowDefinitions.Count>=10);
            WikiUi.SaveOwnWindow(this,output,"wiki-level-detail.png");ShowSearch();
            query.Text="红宝石";debounce.Stop();Search();
            checks["compact direct results"]=results.Items.Count>0&&Height<=320;
            checks["typing does not create detail"]=!showingDetail&&detailView is null;
            checks["local result icon"]=data.Find("红宝石")[0].Icon is not null;
            WikiUi.SaveOwnWindow(this,output,"wiki-search.png");
            var windowCount=app.Windows.Count;Open(data.Find("红宝石").First(e=>e.Path=="/gems#ruby"));
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            checks["detail reuses same native window"]=app.Windows.Count==windowCount&&new System.Windows.Interop.WindowInteropHelper(this).Handle==handle;
            CheckOutsidePointer(-100,-100,1);DismissOutside();
            checks["detail survives outside clicks and stops background pointer polling"]=!closed&&!outsideWatch.IsEnabled&&showingDetail;previousButtons=0;
            checks["specific detail with local icon"]=detailView!.HeroHasImage&&detailView.BodyText.Contains("红宝石")&&!detailView.BodyText.Contains("中毒宝石");
            checks["no ad, source clutter or URLs"]=!detailView.BodyText.Contains("广告")&&!detailView.BodyText.Contains("https://");
            WikiUi.SaveOwnWindow(this,output,"wiki-detail.png");
            var fortune=data.Snapshot.Entries.First(e=>e.Path=="/gems#runeword-fortune");var fortuneBlocks=data.Blocks(fortune)!;
            checks["runeword retains exact four columns and multiline applicability"]=fortuneBlocks.Single(b=>b.Kind=="table").Rows is{Length:2} fr&&fr.All(r=>r.Length==4)&&fr[1][2].Text.Contains('\n')&&fr[1][1].Images.Length==2;
            var runeTable=data.Blocks(data.Snapshot.Entries.First(e=>e.Path=="/gems"))!.Where(b=>b.Kind=="table").Select(b=>b.Rows!).First(r=>r[0][0].Text.Contains("符文之语"));
            var runePreview=new Window{Width=780,Height=650,WindowStyle=WindowStyle.None,ShowActivated=false,Content=new ScrollViewer{Content=WikiContentView.Build([new("heading","符文之语"),new("table","",runeTable)]),Background=WikiUi.Brush("#ddc38b")}};runePreview.Show();WikiUi.SaveOwnWindow(runePreview,output,"wiki-runewords-table.png");
            var tableView=Descendants(runePreview).OfType<Grid>().First(g=>g.ColumnDefinitions.Count==4);
            checks["structured table uses one shared grid for header and all rows"]=tableView.RowDefinitions.Count==runeTable.Length&&tableView.Children.Count==runeTable.Sum(r=>r.Length);
            checks["structured table images are inline not only a bottom gallery"]=Descendants(tableView).OfType<Image>().Count()>20;
            runePreview.Close();
            foreach(var path in new[]{"/classes/warrior","/gems#ruby","/gems#runeword-fortune"})
            {
                Open(data.Snapshot.Entries.First(e=>e.Path==path));WikiUi.SaveOwnWindow(this,output,"wiki-layout-"+path.Split('/').Last().Replace('#','-')+".png");
                checks["structured entry renders "+path]=Descendants(detailView!).OfType<TextBlock>().Any(t=>t.FontSize==12.5);
            }
            var treeEntry=data.Snapshot.Entries.First(e=>e.Path=="/passive-tree#node-5");Open(treeEntry);
            checks["supplemented tree detail and icon render"]=detailView!.HeroHasImage&&detailView.BodyText.Contains("格挡 +200");
            WikiUi.SaveOwnWindow(this,output,"wiki-tree-node.png");
            ShowSearch();checks["back restores same search popup"]=Content==searchRoot&&!showingDetail&&query.Text=="红宝石";
            checks["back preserves compact width"]=Width==SearchWidth;
            WikiPopupPlacement.Verify(checks);
            var buttonState=1;var bounds=new Rect(100,100,300,100);
            checks["opening held click does not dismiss"]=!NewOutsidePress(bounds,new Point(0,0),1,ref buttonState);
            NewOutsidePress(bounds,new Point(0,0),0,ref buttonState);
            checks["inside click remains open"]=!NewOutsidePress(bounds,new Point(150,120),1,ref buttonState);
            checks["dragging outside while held is not new click"]=!NewOutsidePress(bounds,new Point(0,0),1,ref buttonState);
            NewOutsidePress(bounds,new Point(0,0),0,ref buttonState);
            checks["outside right click detected"]=NewOutsidePress(bounds,new Point(0,0),2,ref buttonState);
            query.Text="宝石";debounce.Stop();Search();results.SelectedIndex=1;var selected=((WikiEntry)results.SelectedItem).Path;
            OpenSelected();checks["enter preserves arrow-selected result"]=detailView!.EntryPath==selected;ShowSearch();
            query.Text="血腥斩";debounce.Stop();Search();checks["next query works"]=results.Items.Cast<WikiEntry>().Any(e=>e.Title=="血腥斩");
            query.Clear();debounce.Stop();Search();checks["empty query is only search bar"]=dropdown.Visibility==Visibility.Collapsed&&Height<=50;
            var launcher=new WikiLauncherWindow{Owner=this};var clicks=0;launcher.Requested+=()=>clicks++;launcher.Show();launcher.InvokeForTest();launcher.Follow(100,100,360,1,1);
            var flags=System.Text.Json.JsonSerializer.SerializeToElement(Native.OverlayFlags(new System.Windows.Interop.WindowInteropHelper(launcher).Handle));
            checks["small launcher remains nonactivating clickable"]=clicks==1&&!flags.GetProperty("ClickThrough").GetBoolean()&&flags.GetProperty("NoActivate").GetBoolean();
            var launcherX=100+360-WikiLauncherWindow.WikiWidth-9;
            checks["launcher relative position"]=Native.OverlayIsAt(new System.Windows.Interop.WindowInteropHelper(launcher).Handle,launcherX,107,WikiLauncherWindow.WikiWidth,WikiLauncherWindow.ButtonHeight);
            using(var me=System.Diagnostics.Process.GetCurrentProcess())
            {
                var launcherHandle=new System.Windows.Interop.WindowInteropHelper(launcher).Handle;
                var target=Native.Windows(me.MainModule!.FileName).Single(w=>Native.Handle(w)==launcherHandle);
                checks["launcher occupies native clickable target"]=Native.PointerTargets(target,launcherX+WikiLauncherWindow.WikiWidth/2,118);
            }
            launcher.Close();
            var gear=new WikiLauncherWindow(settingsButton:true){Owner=this};var settingsClicks=0;gear.Requested+=()=>settingsClicks++;gear.Show();gear.InvokeForTest();gear.Follow(100,100,OverlayWindow.BaseWidth,1,1,OverlayWindow.BaseHeight);
            checks["settings small button invokes action"]=settingsClicks==1;
            checks["settings button is bottom right"]=Native.OverlayIsAt(new System.Windows.Interop.WindowInteropHelper(gear).Handle,100+OverlayWindow.BaseWidth-22-9,100+OverlayWindow.BaseHeight-22-7,22,22);
            var settingsWindow=CompanionShell.NewSettingsWindow();gear.Requested+=()=>settingsWindow.Show();gear.InvokeForTest();
            checks["settings action opens visible topmost window"]=settingsWindow.IsVisible&&settingsWindow.Topmost;
            settingsWindow.Close();
            var liveSettings=new CompanionSettings();var applied=0;var persisted=0;var settingsFailure=false;
            var liveWindow=CompanionShell.NewSettingsWindow();
            CompanionShell.BuildSettings(liveWindow,liveSettings,()=>applied++,()=>persisted++, _=>settingsFailure=true);
            liveWindow.Show();liveWindow.UpdateLayout();
            IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {
                yield return parent;for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(parent,i)))yield return child;
            }
            var controls=Descendants(liveWindow).ToArray();var sliders=controls.OfType<Slider>().ToArray();
            sliders[0].Value=.52;sliders[1].Value=1.2;
            controls.OfType<Button>().Single(b=>b.Content is string text&&text=="下方").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["settings sliders and side apply before save"]=applied==3&&persisted==0&&liveSettings.Opacity==.52&&liveSettings.Scale==1.2&&liveSettings.DockSide=="bottom";
            checks["settings have no separate wiki direction picker"]=!controls.OfType<ComboBox>().Any();
            await Task.Delay(450);
            checks["settings automatically persist debounced changes"]=persisted==1&&!settingsFailure;
            WikiUi.SaveOwnWindow(liveWindow,output,"settings-game-style.png");
            sliders[0].Value=.6;liveWindow.Close();
            checks["settings closing flushes pending changes"]=persisted==2;
            var pathSettings=new CompanionSettings();var pathsApplied=0;var pathsSaved=0;var locateCalls=0;string? selection=null;
            var pathWindow=CompanionShell.NewSettingsWindow();
            CompanionShell.BuildSettings(pathWindow,pathSettings,()=>pathsApplied++,()=>pathsSaved++,_=>settingsFailure=true,selectConnectionFile:(_,_)=>selection,locateGame:()=>{locateCalls++;return Environment.ProcessPath;});
            pathWindow.Show();pathWindow.UpdateLayout();Descendants(pathWindow).OfType<Expander>().Single().IsExpanded=true;pathWindow.UpdateLayout();
            var pathControls=Descendants(pathWindow).ToArray();var fields=pathControls.OfType<TextBox>().Where(t=>t.Tag is string).ToArray();
            Button PathButton(string tag)=>pathControls.OfType<Button>().Single(b=>b.Tag as string==tag);
            checks["connection defaults resolve without storing manual override"]=pathSettings.GameExe==""&&pathSettings.SavePath==""&&pathsApplied==0&&fields.Single(t=>t.Tag as string=="game-path").Text==Environment.ProcessPath;
            checks["both paths use file browsing instead of free typing"]=fields.Length==2&&fields.All(t=>t.IsReadOnly)&&pathControls.OfType<Button>().Count(b=>b.Content as string=="浏览文件")==2;
            var gameDialog=CompanionShell.ConnectionDialog(true,Environment.ProcessPath);var saveDialog=CompanionShell.ConnectionDialog(false,null);
            checks["native file pickers filter executable and save files"]=gameDialog.Filter.Contains("*.exe")&&saveDialog.Filter.Contains("*.json")&&gameDialog.CheckFileExists&&saveDialog.CheckFileExists&&gameDialog.CheckPathExists&&saveDialog.CheckPathExists&&!gameDialog.Multiselect&&!saveDialog.Multiselect&&gameDialog.RestoreDirectory&&saveDialog.RestoreDirectory;
            selection=Environment.ProcessPath;PathButton("browse-game").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["selected game file immediately applies"]=pathSettings.GameExe==Environment.ProcessPath&&pathsApplied==1&&pathsSaved==0;
            var sampleSave=Path.Combine(output,"connection-save.json");File.WriteAllText(sampleSave,"{}");selection=sampleSave;PathButton("browse-save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["selected save file immediately applies separately"]=pathSettings.SavePath==sampleSave&&pathSettings.GameExe==Environment.ProcessPath&&pathsApplied==2;
            selection=null;PathButton("browse-game").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["cancel file selection preserves both paths"]=pathSettings.SavePath==sampleSave&&pathSettings.GameExe==Environment.ProcessPath&&pathsApplied==2;
            selection=sampleSave;PathButton("browse-game").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["wrong file type cannot replace game path"]=pathSettings.GameExe==Environment.ProcessPath&&pathsApplied==2;
            PathButton("auto-game").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["automatic game search clears only game override"]=pathSettings.GameExe==""&&pathSettings.SavePath==sampleSave&&pathsApplied==3;
            PathButton("auto-save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var beforeRetry=locateCalls;PathButton("auto-game").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["retry automatic search does not lock discovered paths"]=pathSettings.GameExe==""&&pathSettings.SavePath==""&&pathsApplied==4&&locateCalls==beforeRetry+1;
            await Task.Delay(450);checks["connection changes save once after debounce"]=pathsSaved==1&&!settingsFailure;
            WikiUi.SaveOwnWindow(pathWindow,output,"settings-file-browsing.png");pathWindow.Close();
            var reminderSettings=new CompanionSettings();var reminderChanges=0;var reminderWindow=CompanionShell.NewSettingsWindow();var potionPreviews=0;var bossPreviews=0;
            CompanionShell.BuildSettings(reminderWindow,reminderSettings,()=>reminderChanges++,()=>{},_=>{},()=>potionPreviews++,()=>bossPreviews++);reminderWindow.Show();reminderWindow.UpdateLayout();
            var reminderControls=Descendants(reminderWindow).ToArray();var checkbox=reminderControls.OfType<CheckBox>().Single(c=>c.Content as string=="巨像提醒");
            checks["colossus reminders default off and advanced paths collapsed"]=checkbox.IsChecked==false&&reminderControls.OfType<Expander>().Single().IsExpanded==false;
            checkbox.IsChecked=true;reminderControls.OfType<RadioButton>().Single(r=>r.Content as string=="提前 5 分钟").IsChecked=true;
            checks["reminder preferences immediately apply"]=reminderSettings.ColossusReminderEnabled&&reminderSettings.ColossusReminderMinutes==5&&reminderChanges==2;
            var potionCheck=reminderControls.OfType<CheckBox>().Single(c=>c.Content as string=="补药水提醒");potionCheck.IsChecked=true;
            checks["potion reminder setting applies immediately"]=reminderSettings.PotionReminderEnabled&&reminderChanges==3;
            var settingsBeforePreview=System.Text.Json.JsonSerializer.Serialize(reminderSettings);
            reminderControls.OfType<Button>().Single(b=>b.Tag as string=="test-potion").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));reminderControls.OfType<Button>().Single(b=>b.Tag as string=="test-colossus").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["both reminder previews invoke without changing settings or formal dedupe records"]=potionPreviews==1&&bossPreviews==1&&reminderChanges==3&&System.Text.Json.JsonSerializer.Serialize(reminderSettings)==settingsBeforePreview;
            WikiUi.SaveOwnWindow(reminderWindow,output,"settings-afk-reminder.png");reminderWindow.Close();
            gear.Close();
            var epoch=DateTimeOffset.FromUnixTimeMilliseconds(0);
            checks["session clock hours not zero padded"]=OverlayWindow.SessionDuration(epoch,epoch.AddHours(2).AddMinutes(6).AddSeconds(9))=="2小时：06分：09秒";
            checks["session clock days only after whole day"]=OverlayWindow.SessionDuration(epoch,epoch.AddDays(1).AddHours(2))=="1天：2小时：00分：00秒";
            checks["future session clock clamps zero"]=OverlayWindow.SessionDuration(epoch.AddHours(1),epoch)=="0小时：00分：00秒";
            checks["session clock updates second and day boundaries"]=OverlayWindow.SessionDuration(epoch,epoch.AddSeconds(60))=="0小时：01分：00秒"&&OverlayWindow.SessionDuration(epoch,epoch.AddSeconds(86399))=="23小时：59分：59秒";
            var updateRelease=new UpdateRelease("v0.2","修复窗口跟随\n改进搜索",new Uri("https://github.com/sangshiCHI/QATracker/releases/download/v0.2/QATK.exe"),new string('A',64),100);
            var updateDialog=CompanionShell.NewUpdateWindow(updateRelease,out var updateButton,out var updateTitle,out var updateProgress,out var updateStatus,out var updateLater);updateDialog.Show();updateDialog.UpdateLayout();
            checks["automatic update prompt does not activate game away"]=!updateDialog.ShowActivated&&!updateDialog.IsActive;
            checks["update dialog states old new version and changelog"]=updateTitle.Text.Contains("0.1")&&updateTitle.Text.Contains("v0.2")&&Descendants(updateDialog).OfType<TextBox>().Any(t=>t.IsReadOnly&&t.Text==updateRelease.Notes);
            checks["update dialog has explicit optional consent"]=updateButton.Content as string=="立即更新"&&Descendants(updateDialog).OfType<Button>().Any(b=>b.Content as string=="稍后");
            checks["download progress hidden until consent"]=updateProgress.Visibility==Visibility.Collapsed&&updateStatus.Text.Contains("取消");
            WikiUi.SaveOwnWindow(updateDialog,output,"update-dialog-controlled.png");
            updateProgress.Visibility=Visibility.Visible;updateProgress.Value=42;updateStatus.Text="正在下载 42.0 / 100.0 MB（42%）";updateLater.Content="取消下载";updateButton.IsEnabled=false;updateButton.Content="下载中…";updateDialog.UpdateLayout();
            checks["download UI retains version and notes while showing cancel and progress"]=updateTitle.Text.Contains("v0.2")&&updateProgress.Value==42&&!updateButton.IsEnabled&&updateLater.Content as string=="取消下载"&&Descendants(updateDialog).OfType<TextBox>().Any(t=>t.Text==updateRelease.Notes);
            WikiUi.SaveOwnWindow(updateDialog,output,"update-progress-controlled.png");updateLater.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["cancel action closes update dialog"]=!updateDialog.IsVisible;
            checks["invalid saved timestamp stays unknown"]=OverlayWindow.SavedClock(long.MaxValue)=="—"&&OverlayWindow.SavedClock(null)=="—";
            var anchor=new Window{Title="QATK owned statistics fixture",Width=360,Height=255,Left=200,Top=200,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,Background=Brushes.SaddleBrown};
            var game=new Window{Title="QATK owned game-area fixture",Width=340,Height=140,Left=568,Top=220,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,Background=Brushes.DarkSlateBlue};
            anchor.Show();game.Show();
            AdditionalExclusions=()=>[new Rect(game.PointToScreen(new Point(0,0)),game.PointToScreen(new Point(game.ActualWidth,game.ActualHeight)))];
            PlaceNear(anchor);
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var anchorBounds=new Rect(anchor.PointToScreen(new Point(0,0)),anchor.PointToScreen(new Point(anchor.ActualWidth,anchor.ActualHeight)));
            var popupBounds=new Rect(PointToScreen(new Point(0,0)),PointToScreen(new Point(ActualWidth,ActualHeight)));
            checks["actual owned popup does not cover stats fixture"]=!anchorBounds.IntersectsWith(popupBounds);
            checks["actual owned popup avoids game-area fixture"]=!AdditionalExclusions()[0].IntersectsWith(popupBounds);
            Program.Save(output,"owned-popup-placement.json",new{Synthetic=true,Anchor=anchorBounds,Game=AdditionalExclusions()[0],Popup=popupBounds,Notice="Owned WPF windows, not real game DPI acceptance"});
            anchorWindow=null;AdditionalExclusions=null;anchor.Close();game.Close();
            var dismiss=new WikiWindow();dismiss.Show();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            checks["outside watcher starts while popup open"]=dismiss.outsideWatch.IsEnabled;
            dismiss.CheckOutsidePointer(0,0,0);var origin=dismiss.PointToScreen(new Point(0,0));
            dismiss.CheckOutsidePointer((int)origin.X-30,(int)origin.Y-30,1);
            checks["outside click closes popup window"]=dismiss.closed&&!dismiss.outsideWatch.IsEnabled;
            Program.Save(output,"wiki-ui-result.json",new{Passed=checks.Values.All(v=>v),Cases=checks.Count,Checks=checks,Notice="Owned WPF; same-HWND popup; no browser or game input"});
            app.Shutdown(checks.Values.All(v=>v)?0:1);
        }
        catch(Exception e){Program.Save(output,"wiki-ui-error.json",new{Error=e.ToString()});app.Shutdown(1);}
    }
}
internal sealed class WikiDetailView:UserControl
{
    readonly WikiSearch data;readonly DockPanel body=new();Image hero=WikiUi.Image(null,72);
    public string EntryPath{get;private set;}="";
    public string BodyText{get;private set;}="";
    public bool HeroHasImage=>hero.Source is not null;
    WikiEntry currentEntry;
    public double PreferredHeight{get;private set;}=420;
    public WikiDetailView(WikiSearch data,WikiEntry entry)
    {
        this.data=data;currentEntry=entry;Content=body;SetEntry(entry);
    }
    public void SetEntry(WikiEntry entry)
    {
        currentEntry=entry;EntryPath=entry.Path;BodyText=data.Details(entry);body.Children.Clear();
        PreferredHeight=Math.Clamp(220+BodyText.Split('\n').Length*23+Math.Min(data.Images(entry).Length,12)*10,300,720);
        var header=new DockPanel{Margin=new Thickness(5,4,5,12)};hero=WikiUi.Image(entry.Icon,56);if(hero.Source is not null)header.Children.Add(new Border{BorderBrush=WikiUi.Brush("#ad8540"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Padding=new Thickness(8),Child=hero});
        var titles=new StackPanel{Margin=new Thickness(hero.Source is null?0:15,6,0,0)};titles.Children.Add(new TextBlock{Text=entry.Title,FontSize=21,FontWeight=FontWeights.Bold,Foreground=WikiUi.Brush("#e8c779"),TextWrapping=TextWrapping.Wrap});
        titles.Children.Add(new TextBlock{Text=entry.Category+" · 游戏版本 "+data.Snapshot.GameVersion,Foreground=WikiUi.Brush("#cbb184"),Margin=new Thickness(0,5,0,0)});header.Children.Add(titles);
        DockPanel.SetDock(header,Dock.Top);body.Children.Add(header);
        var structured=data.Blocks(entry);
        var content=structured is{Length:>0}?WikiContentView.Build(structured):new StackPanel{Margin=new Thickness(12)};
        foreach(var line in structured is{Length:>0}?Array.Empty<string>():BodyText.Split('\n'))
        {
            var text=line.Trim();if(text.Length==0)continue;
            if(text.Contains('｜'))
            {
                var row=new Grid{Margin=new Thickness(0,2,0,2)};var cells=text.Split('｜');
                for(var i=0;i<cells.Length;i++)
                {
                    row.ColumnDefinitions.Add(new(){Width=new GridLength(i==0?2:1,GridUnitType.Star)});
                    var cell=new Border{Background=WikiUi.Brush("#49321e"),BorderBrush=WikiUi.Brush("#725431"),BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(5,7,5,7),
                        Child=new TextBlock{Text=cells[i].Trim(),FontSize=13,Foreground=WikiUi.Brush("#f1e2c3"),TextWrapping=TextWrapping.Wrap}};
                    Grid.SetColumn(cell,i);row.Children.Add(cell);
                }
                content.Children.Add(row);continue;
            }
            var heading=text.StartsWith('#');var paragraph=new TextBlock{Text=heading?text.TrimStart('#',' '):text,TextWrapping=TextWrapping.Wrap,FontSize=14,Foreground=WikiUi.Brush("#352514"),Margin=new Thickness(0,heading?14:3,0,4)};
            if(heading){paragraph.FontSize=17;paragraph.FontWeight=FontWeights.Bold;paragraph.Foreground=WikiUi.Brush("#63431e");}
            content.Children.Add(paragraph);
        }
        var inlineImages=(structured??[]).SelectMany(b=>(b.Rows??[]).SelectMany(r=>r).SelectMany(c=>c.Images)).ToHashSet();
        var pictures=WikiPictures.Local.Value;var paths=data.Images(entry).Where(p=>p!=(entry.IconPath??pictures.IconPath(entry.Path))&&!inlineImages.Contains(p)).Distinct().ToArray();
        if(paths.Length>0)
        {
            content.Children.Add(new TextBlock{Text="相关插图",FontSize=16,Foreground=WikiUi.Brush("#63431e"),Margin=new Thickness(8,16,0,8)});
            var gallery=new WrapPanel();content.Children.Add(gallery);var offset=0;var more=WikiUi.Button("更多插图");more.HorizontalAlignment=HorizontalAlignment.Left;more.Margin=new Thickness(8);
            void AddImages(){foreach(var path in paths.Skip(offset).Take(12)){var bitmap=pictures.Get(path);if(bitmap is null)continue;var button=WikiUi.Button("");button.Content=WikiUi.Image(bitmap,64);button.Margin=new Thickness(4);button.ToolTip="点击放大本地插图";button.Click+=(_,_)=>Zoom(path);gallery.Children.Add(button);}offset+=12;more.Visibility=offset<paths.Length?Visibility.Visible:Visibility.Collapsed;}
            more.Click+=(_,_)=>AddImages();AddImages();content.Children.Add(more);
        }
        body.Children.Add(new Border{Background=WikiUi.Brush("#ddc38b"),CornerRadius=new CornerRadius(5),Child=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled}});
    }
    void Zoom(string path)
    {
        var image=WikiPictures.Local.Value.Get(path,1000);if(image is null)return;
        body.Children.Clear();var back=WikiUi.Button("‹ 返回资料");back.Click+=(_,_)=>SetEntry(currentEntry);DockPanel.SetDock(back,Dock.Top);body.Children.Add(back);
        body.Children.Add(new ScrollViewer{Content=new Image{Source=image,Stretch=Stretch.Uniform},VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
    }
}

