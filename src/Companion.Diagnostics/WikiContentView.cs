using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Text.RegularExpressions;

namespace Companion.Diagnostics;

internal static class WikiContentView
{
    internal static StackPanel Build(WikiBlock[] blocks)
    {
        var panel=new StackPanel{Margin=new Thickness(12)};
        foreach(var block in blocks)
        {
            if(block.Kind=="table"&&block.Rows is{Length:>0}){panel.Children.Add(Table(block.Rows));continue;}
            if(string.IsNullOrWhiteSpace(block.Text))continue;
            if(block.Kind=="heading")
                panel.Children.Add(new Border{Background=WikiUi.Brush("#49301b"),CornerRadius=new CornerRadius(5),Padding=new Thickness(10,8,10,8),Margin=new Thickness(0,14,0,8),Child=new TextBlock{Text=block.Text,Foreground=WikiUi.Brush("#f3d493"),FontSize=17,FontWeight=FontWeights.Bold,TextWrapping=TextWrapping.Wrap}});
            else panel.Children.Add(new TextBlock{Text=block.Text,Foreground=WikiUi.Brush("#3b2918"),FontSize=13,LineHeight=21,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(2,3,2,7)});
        }
        return panel;
    }
    internal static Grid Table(WikiCell[][] rows)
    {
        var grid=new Grid{Margin=new Thickness(0,3,0,12)};var occupied=new HashSet<(int Row,int Col)>();var placements=new List<(int Row,int Col,WikiCell Cell)>();var columns=0;
        for(var r=0;r<Math.Min(rows.Length,1000);r++)
        {
            grid.RowDefinitions.Add(new(){Height=GridLength.Auto});var col=0;
            foreach(var cell in rows[r])
            {
                while(occupied.Contains((r,col)))col++;
                if(col>=32)break;
                var cs=Math.Clamp(cell.ColSpan,1,32-col);var rs=Math.Clamp(cell.RowSpan,1,rows.Length-r);
                placements.Add((r,col,cell with{ColSpan=cs,RowSpan=rs}));
                for(var y=r;y<r+rs;y++)for(var x=col;x<col+cs;x++)occupied.Add((y,x));
                col+=cs;columns=Math.Max(columns,col);
            }
        }
        for(var col=0;col<columns;col++)
        {
            var title=placements.FirstOrDefault(p=>p.Row==0&&p.Col==col).Cell?.Text??"";
            var weight=columns==4?(col==0?1:title.Contains("宝石")?2.2:title.Contains("适用")?2.6:2.4):col==0?1.7:1;
            grid.ColumnDefinitions.Add(new(){Width=new GridLength(weight,GridUnitType.Star)});
        }
        foreach(var (r,c,cell) in placements)
        {
            var content=new StackPanel();
            if(cell.Images.Length>0)
            {
                var icons=new WrapPanel{Margin=new Thickness(0,0,0,5)};
                foreach(var path in cell.Images.Take(16))
                {
                    var image=WikiPictures.Local.Value.Get(path);if(image is null)continue;
                    icons.Children.Add(new Border{Background=WikiUi.Brush("#48321b"),BorderBrush=WikiUi.Brush("#977139"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(2),Margin=new Thickness(0,0,3,2),Child=WikiUi.Image(image,26)});
                }
                content.Children.Add(icons);
            }
            var text=new TextBlock{FontSize=12.5,LineHeight=20,TextWrapping=TextWrapping.Wrap,Foreground=WikiUi.Brush(cell.Header?"#fff0d0":"#352514"),FontWeight=cell.Header?FontWeights.Bold:FontWeights.Normal};
            foreach(var piece in Regex.Split(cell.Text,"(普通|优秀|稀有|史诗|神话|传说|永恒)"))
            {
                var q=SaveSource.Qualities.FirstOrDefault(q=>AppResources.Text(q)==piece);
                if(q is not null&&!cell.Header)text.Inlines.Add(new InlineUIContainer(new Border{Background=WikiUi.Brush("#422712"),CornerRadius=new CornerRadius(7),Padding=new Thickness(5,0,5,0),Margin=new Thickness(1,1,1,1),Child=new TextBlock{Text=piece,Foreground=WikiUi.Brush(AppResources.Color(q)),FontSize=11,FontWeight=FontWeights.SemiBold}}){BaselineAlignment=BaselineAlignment.Center});
                else text.Inlines.Add(new Run(piece));
            }
            content.Children.Add(text);
            var border=new Border{Background=WikiUi.Brush(cell.Header?"#52361e":r%2==0?"#d8bc80":"#e4cc96"),BorderBrush=WikiUi.Brush("#b79554"),BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(7,8,7,8),Child=content};
            Grid.SetRow(border,r);Grid.SetColumn(border,c);Grid.SetRowSpan(border,cell.RowSpan);Grid.SetColumnSpan(border,cell.ColSpan);grid.Children.Add(border);
        }
        return grid;
    }
}
