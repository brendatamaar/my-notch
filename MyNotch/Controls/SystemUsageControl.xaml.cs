using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MyNotch.Models;
using MyNotch.Services;
using Windows.Foundation;
using Windows.UI;

namespace MyNotch.Controls;

public sealed partial class SystemUsageControl : UserControl, IDisposable
{
    private readonly SystemUsageService _service;
    private readonly List<SystemUsageSnapshot> _history = new();
    private readonly Dictionary<SystemModule, (Button Button, TextBlock Value, Canvas Chart)> _chips = new();
    private SystemUsageSnapshot? _latest;
    private SystemModule _module = SystemModule.Cpu;
    private bool _active, _rendering;
    private int _listPage;
    public SystemUsageControl()
    {
        InitializeComponent();
        _service = new(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _service.SamplingFailed += (_, _) => SourceLabel.Text = "System reading temporarily unavailable";
        _service.Changed += (_, snapshot) => { _latest = snapshot; _history.Add(snapshot); if (_history.Count > 60) _history.RemoveAt(0); Render(); };
        foreach (var module in Enum.GetValues<SystemModule>())
        {
            ModuleStrip.ColumnDefinitions.Add(new ColumnDefinition());
            var stack = new StackPanel { Spacing = 0 };
            stack.Children.Add(new TextBlock { Text = ModuleName(module), FontSize = 9, Foreground = Brush(145,149,159) });
            var value = new TextBlock { Text = "--", FontSize = 12, Foreground = Brush(240,240,245) };
            var chart = new Canvas { Height = 7 };
            stack.Children.Add(value); stack.Children.Add(chart);
            var button = new Button { Content = stack, Style = (Style)Application.Current.Resources["NotchButtonStyle"], MinWidth = 0, Padding = new Thickness(7,2,7,2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, ModuleName(module));
            button.Click += (_, _) => { _module = module; _listPage = 0; Render(); };
            Grid.SetColumn(button, (int)module); ModuleStrip.Children.Add(button); _chips[module] = (button,value,chart);
        }
    }
    public void Start() { if (_active) return; _active = true; _history.Clear(); _service.Start(); }
    public void Stop() { _active = false; _service.Stop(); }
    public void Dispose() => _service.Dispose();
    private void PreviousList_Click(object sender, RoutedEventArgs e) { _listPage--; Render(); }
    private void NextList_Click(object sender, RoutedEventArgs e) { _listPage++; Render(); }
    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) { if (!_rendering) Render(); }
    private static string ModuleName(SystemModule module) => module switch { SystemModule.Cpu => "CPU", SystemModule.Gpu => "GPU", SystemModule.Memory => "Memory", SystemModule.Drives => "Drives", SystemModule.Network => "Network", _ => "Power" };
    private static Color Accent(SystemModule module) => module switch { SystemModule.Memory => Color.FromArgb(255,167,139,250), SystemModule.Drives => Color.FromArgb(255,255,191,112), SystemModule.Power => Color.FromArgb(255,76,214,146), _ => Color.FromArgb(255,105,173,255) };
    private static double? Value(SystemUsageSnapshot s, SystemModule module) => module switch
    {
        SystemModule.Cpu => s.CpuPercent,
        SystemModule.Gpu => s.GpuPercent,
        SystemModule.Memory => s.MemoryTotal is > 0 && s.MemoryUsed is {} used ? 100d*used/s.MemoryTotal.Value : null,
        SystemModule.Drives => s.StorageTotal is > 0 && s.StorageUsed is {} stored ? 100d*stored/s.StorageTotal.Value : null,
        SystemModule.Network => s.DownloadRate,
        SystemModule.Power => s.Power is {HasBattery:true} p ? p.BatteryPercent : null,
        _ => null,
    };
    private static string Percent(double? value) => value is {} n ? $"{n:0}%" : "--";
    private static string Rate(double? value) => value is null ? "--" : SystemUsageMath.Bytes(value)+"/s";
    private static string Uptime(TimeSpan value) => $"{value.Days}d {value.Hours}h {value.Minutes}m";
    private void Render()
    {
        if (_latest is not {} s || _rendering) return;
        _rendering = true;
        try
        {
            foreach (var (module,chip) in _chips)
            {
                chip.Button.Background = module == _module ? Brush(33,35,42) : Brush(14,15,18);
                chip.Value.Text = module == SystemModule.Network ? Rate(s.DownloadRate) : module == SystemModule.Power && s.Power is {HasBattery:false} ? s.Power.OnAc ? "AC" : "--" : Percent(Value(s,module));
                Draw(chip.Chart,_history.Select(h=>Value(h,module)).ToArray(),Accent(module),module==SystemModule.Network?null:100);
            }
            var percent = Value(s,_module);
            ModuleTitle.Text = _module == SystemModule.Power && s.Power is {HasBattery:true} ? "Battery" : ModuleName(_module);
            Ring.Stroke = new SolidColorBrush(Accent(_module));
            bool ring = _module != SystemModule.Network && !(_module == SystemModule.Power && s.Power is not {HasBattery:true});
            RingTrack.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
            Ring.Visibility = ring && percent is not null ? Visibility.Visible : Visibility.Collapsed;
            double circumference = 2*Math.PI*35/6;
            Ring.StrokeDashArray = new DoubleCollection { circumference*(percent??0)/100,circumference+1 };
            MainValue.FontSize = _module==SystemModule.Network?16:20;
            MainValue.Text = _module==SystemModule.Network?Rate(s.DownloadRate):_module==SystemModule.Power && s.Power is {HasBattery:false} ? s.Power.OnAc?"AC":"--":Percent(percent);
            MainCaption.Text = _module switch {SystemModule.Cpu=>"total load",SystemModule.Gpu=>"busiest engine",SystemModule.Memory or SystemModule.Drives=>"used",SystemModule.Network=>"download",_=>s.Power is {HasBattery:true}?"remaining":"power source"};
            DetailsHost.Children.Clear(); ListHost.Children.Clear(); ListPager.Visibility=Visibility.Collapsed;
            string bytes(ulong? n)=>n is {} v?SystemUsageMath.Bytes(v):"--";
            switch(_module)
            {
                case SystemModule.Cpu:
                    Detail("User",Percent(s.CpuUserPercent),Accent(SystemModule.Cpu)); Detail("System",Percent(s.CpuSystemPercent),Color.FromArgb(255,241,112,117)); Detail("Idle",Percent(s.CpuPercent is {} load?100-load:null)); Detail("Logical cores",Environment.ProcessorCount.ToString()); Detail("Uptime",Uptime(s.Uptime));
                    SourceLabel.Text=s.CpuName; Processes(s.Processes.OrderByDescending(p=>p.CpuPercent??0).ThenByDescending(p=>p.MemoryBytes),p=>Percent(p.CpuPercent)); break;
                case SystemModule.Gpu:
                    Detail("Load",Percent(s.GpuPercent),Accent(SystemModule.Gpu)); Detail("Scope","All GPU engines"); Detail("Uptime",Uptime(s.Uptime));
                    SourceLabel.Text=s.GpuPercent is null?"Windows has no GPU counter reading":s.GpuName;
                    Processes(s.Processes.Where(p=>p.GpuPercent is > 0).OrderByDescending(p=>p.GpuPercent),p=>Percent(p.GpuPercent)); break;
                case SystemModule.Memory:
                    Detail("Used",bytes(s.MemoryUsed),Accent(SystemModule.Memory)); Detail("Available",s.MemoryTotal is {} total&&s.MemoryUsed is {} used?SystemUsageMath.Bytes(total-used):"--"); Detail("Total",bytes(s.MemoryTotal)); Detail("Committed",bytes(s.CommitUsed)); Detail("Commit limit",bytes(s.CommitTotal));
                    SourceLabel.Text="Physical RAM and system commit"; Processes(s.Processes.OrderByDescending(p=>p.MemoryBytes),p=>SystemUsageMath.Bytes(p.MemoryBytes)); break;
                case SystemModule.Drives:
                    Detail("Drive",s.Drive); Detail("Used",SystemUsageMath.Bytes(s.StorageUsed),Accent(SystemModule.Drives)); Detail("Free",s.StorageTotal is {} dt&&s.StorageUsed is {} du?SystemUsageMath.Bytes(dt-du):"--"); Detail("Total",SystemUsageMath.Bytes(s.StorageTotal)); Detail("Mounted",s.Drives.Count.ToString());
                    SourceLabel.Text="Storage capacity; refreshed every 30s"; ListTitle.Text="MOUNTED DRIVES";
                    var drives=Page(s.Drives,3); foreach(var d in drives){ListLine(d.Name,$"{SystemUsageMath.Bytes(d.Total-d.Used)} free",100d*d.Used/Math.Max(1,d.Total));} break;
                case SystemModule.Network:
                    Detail("Download",Rate(s.DownloadRate),Accent(SystemModule.Network)); Detail("Upload",Rate(s.UploadRate),Color.FromArgb(255,167,139,250)); Detail("Active links",s.Connections.ToString()); Detail("Uptime",Uptime(s.Uptime));
                    SourceLabel.Text="Combined active adapter traffic"; ListTitle.Text="CONNECTIONS";
                    foreach(var a in Page(s.Adapters,3))ListLine(a.Name,$"{a.Address}  {SystemUsageMath.Bytes(a.LinkBitsPerSecond/8d)}/s link"); break;
                case SystemModule.Power:
                    Detail("Supply",s.Power is {} power?power.OnAc?"AC power":power.HasBattery?"Battery":"Unknown":"--"); Detail("Status",s.Power is {Charging:true}?"Charging":s.Power is {HasBattery:true,OnAc:true}?"Plugged in":s.Power is {HasBattery:true}?"On battery":"No battery");
                    Detail("Time left",s.Power?.Remaining is {} remaining?Uptime(remaining):"--"); Detail("Uptime",Uptime(s.Uptime));
                    SourceLabel.Text=s.Power is {HasBattery:true}?"Battery status reported by Windows":"Power draw is not reported by Windows";
                    ListTitle.Text="SYSTEM"; ListLine("Processes",s.ProcessCount.ToString()); ListLine("Architecture",System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()); ListLine("Windows",Environment.OSVersion.Version.ToString()); break;
            }
            ChartScale.Text=_module==SystemModule.Network?"auto scale":"0-100%";
            Draw(HistoryChart,_history.Select(h=>Value(h,_module)).ToArray(),Accent(_module),_module==SystemModule.Network?null:100);
            SecondaryTitle.Text=_module switch {SystemModule.Cpu=>"USER / SYSTEM",SystemModule.Network=>"UPLOAD",SystemModule.Memory=>"AVAILABLE MEMORY",SystemModule.Drives=>"FREE CAPACITY",_=>"ACTIVITY"};
            var second=_history.Select(h=>_module switch {SystemModule.Cpu=>h.CpuUserPercent,SystemModule.Network=>h.UploadRate,SystemModule.Memory or SystemModule.Drives=>Value(h,_module) is {} v?100-v:null,_=>Value(h,_module)}).ToArray();
            Draw(SecondaryChart,second,_module==SystemModule.Cpu?Accent(SystemModule.Cpu):Color.FromArgb(255,167,139,250),_module==SystemModule.Network?null:100);
            if(_module==SystemModule.Cpu)Draw(SecondaryChart,_history.Select(h=>h.CpuSystemPercent).ToArray(),Color.FromArgb(255,241,112,117),100,false);
        }
        finally{_rendering=false;}
    }
    private void Detail(string label,string value,Color? color=null)
    {
        var row=new Grid {ColumnSpacing=4}; row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var name=new TextBlock{Text=label,FontSize=10,Foreground=new SolidColorBrush(color??Color.FromArgb(255,143,147,158))};
        var text=new TextBlock{Text=value,FontSize=10,Foreground=Brush(225,227,234)};Grid.SetColumn(text,1);row.Children.Add(name);row.Children.Add(text);DetailsHost.Children.Add(row);
    }
    private void Processes(IEnumerable<ProcessUsage> items,Func<ProcessUsage,string> value)
    {
        ListTitle.Text="TOP PROCESSES";
        var list=items.Take(5).ToArray();
        foreach(var item in list)ListLine(item.Name,value(item));
        if(list.Length==0)ListLine("No active processes reported","");
    }
    private IReadOnlyList<T> Page<T>(IReadOnlyList<T> items,int size)
    {
        int pages=Math.Max(1,(items.Count+size-1)/size);_listPage=Math.Clamp(_listPage,0,pages-1);
        ListPager.Visibility=pages>1?Visibility.Visible:Visibility.Collapsed;ListPageLabel.Text=$"{_listPage+1}/{pages}";PreviousList.IsEnabled=_listPage>0;NextList.IsEnabled=_listPage+1<pages;
        return items.Skip(_listPage*size).Take(size).ToArray();
    }
    private void ListLine(string label,string value,double? bar=null)
    {
        var row=new StackPanel{Spacing=2};var title=new TextBlock{Text=label,FontSize=11,Foreground=Brush(228,230,236),TextTrimming=TextTrimming.CharacterEllipsis,MaxLines=1};ToolTipService.SetToolTip(title,label);row.Children.Add(title);
        if(value.Length>0){var text=new TextBlock{Text=value,FontSize=9,Foreground=Brush(143,147,158),TextTrimming=TextTrimming.CharacterEllipsis,MaxLines=1};ToolTipService.SetToolTip(text,value);row.Children.Add(text);}
        if(bar is {} percentage)row.Children.Add(new ProgressBar{Value=percentage,Maximum=100,Height=3,MinHeight=3,Foreground=new SolidColorBrush(Accent(_module)),Background=Brush(35,37,44)});
        ListHost.Children.Add(row);
    }
    private static void Draw(Canvas canvas,IReadOnlyList<double?> values,Color color,double? maximum,bool clear=true)
    {
        if(clear)canvas.Children.Clear();
        double width=canvas.ActualWidth>0?canvas.ActualWidth:70,height=canvas.Height;
        double ceiling=maximum??Math.Max(1024,values.Where(v=>v is not null).Select(v=>v!.Value).DefaultIfEmpty(0).Max()*1.15);
        if(height>20&&clear)for(int i=0;i<3;i++)canvas.Children.Add(new Line{X1=0,X2=width,Y1=i*(height-2)/2,Y2=i*(height-2)/2,Stroke=Brush(30,32,39),StrokeThickness=1});
        Polyline? line=null;
        for(int i=0;i<values.Count;i++)
        {
            if(values[i] is not {} value){line=null;continue;}
            if(line is null){line=new Polyline{Stroke=new SolidColorBrush(color),StrokeThickness=height<10?1:1.5,StrokeLineJoin=PenLineJoin.Round};canvas.Children.Add(line);}
            line.Points.Add(new Point(width*i/Math.Max(59,values.Count-1),height-1-Math.Clamp(value/ceiling,0,1)*(height-2)));
        }
    }
    private static SolidColorBrush Brush(byte r,byte g,byte b)=>new(Color.FromArgb(255,r,g,b));
}
