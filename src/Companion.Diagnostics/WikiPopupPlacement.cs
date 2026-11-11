using System.Windows;

namespace Companion.Diagnostics;

internal static class WikiPopupPlacement
{
    internal static readonly string[] Sides=["right","left","top","bottom"];
    internal static string SearchSide(string dockSide)=>dockSide=="bottom"?"bottom":"top";
    internal static Rect? Dock(Rect anchor,Rect work,Size size,string preferred,IReadOnlyList<Rect>? excluded=null,double gap=8,bool allowFallback=true,bool alignBottom=false)
    {
        var obstacles=excluded??[];
        foreach(var side in allowFallback?new[]{preferred}.Concat(Sides).Distinct():new[]{preferred})
        {
            var x=side=="right"?anchor.Right+gap:side=="left"?anchor.Left-gap-size.Width:anchor.Left;
            var y=side=="bottom"?anchor.Bottom+gap:side=="top"?anchor.Top-gap-size.Height:alignBottom?anchor.Bottom-size.Height:anchor.Top;
            if(side is "right" or "left")y=Math.Clamp(y,work.Top,Math.Max(work.Top,work.Bottom-size.Height));
            else x=Math.Clamp(x,work.Left,Math.Max(work.Left,work.Right-size.Width));
            var rect=new Rect(x,y,size.Width,size.Height);
            if(work.Contains(rect)&&!rect.IntersectsWith(anchor)&&!obstacles.Any(rect.IntersectsWith))return rect;
        }
        return null;
    }
    // All geometry is in one coordinate system (physical pixels in production).
    internal static Rect? Choose(Rect anchor,Rect work,Size desired,double gap=8,double minimumWidth=160,double minimumHeight=36,IReadOnlyList<Rect>? excluded=null,string preferred="right")
    {
        if(Dock(anchor,work,desired,preferred,excluded,gap,false) is Rect dock)return dock;
        if(excluded is{Count:>0})return AvoidAll(anchor,work,desired,gap,minimumWidth,minimumHeight,excluded,preferred);
        Rect Space(double x,double y,double w,double h)=>new(x,y,Math.Max(0,w),Math.Max(0,h));
        var right=Math.Clamp(anchor.Right+gap,work.Left,work.Right);
        var left=Math.Clamp(anchor.Left-gap,work.Left,work.Right);
        var bottom=Math.Clamp(anchor.Bottom+gap,work.Top,work.Bottom);
        var top=Math.Clamp(anchor.Top-gap,work.Top,work.Bottom);
        var spaces=new[]{Space(right,work.Top,work.Right-right,work.Height),Space(work.Left,work.Top,left-work.Left,work.Height),
            Space(work.Left,bottom,work.Width,work.Bottom-bottom),Space(work.Left,work.Top,work.Width,top-work.Top)};
        Rect Put(int index,double width,double height)
        {
            var s=spaces[index];
            var x=index==0?s.Left:index==1?s.Right-width:Math.Clamp(anchor.Left,s.Left,s.Right-width);
            var y=index==2?s.Top:index==3?s.Bottom-height:Math.Clamp(anchor.Top,s.Top,s.Bottom-height);
            return new(x,y,width,height);
        }
        for(var i=0;i<spaces.Length;i++)
            if(spaces[i].Width>=desired.Width&&spaces[i].Height>=desired.Height)return Put(i,desired.Width,desired.Height);
        var best=-1;double area=0;
        for(var i=0;i<spaces.Length;i++)
        {
            var width=Math.Min(desired.Width,spaces[i].Width);var height=Math.Min(desired.Height,spaces[i].Height);
            if(width>=minimumWidth&&height>=minimumHeight&&width*height>area){best=i;area=width*height;}
        }
        return best<0?null:Put(best,Math.Min(desired.Width,spaces[best].Width),Math.Min(desired.Height,spaces[best].Height));
    }
    static Rect? AvoidAll(Rect anchor,Rect work,Size desired,double gap,double minimumWidth,double minimumHeight,IReadOnlyList<Rect> extra,string preferred)
    {
        var free=new List<Rect>{work};
        foreach(var obstacle in new[]{anchor}.Concat(extra))
        {
            if(obstacle.IsEmpty)continue;var blocked=obstacle;blocked.Inflate(gap,gap);var next=new List<Rect>();
            foreach(var slot in free)
            {
                var cut=Rect.Intersect(slot,blocked);
                if(cut.IsEmpty||cut.Width==0||cut.Height==0){next.Add(slot);continue;}
                void Add(double x,double y,double w,double h){if(w>=minimumWidth&&h>=minimumHeight)next.Add(new(x,y,w,h));}
                Add(slot.Left,slot.Top,cut.Left-slot.Left,slot.Height);
                Add(cut.Right,slot.Top,slot.Right-cut.Right,slot.Height);
                Add(slot.Left,slot.Top,slot.Width,cut.Top-slot.Top);
                Add(slot.Left,cut.Bottom,slot.Width,slot.Bottom-cut.Bottom);
            }
            free=next;
        }
        var candidates=new List<(Rect Rect,bool Full,int Side,double Distance,double Alignment,double Area)>();
        foreach(var slot in free)
        {
            var width=Math.Min(desired.Width,slot.Width);var height=Math.Min(desired.Height,slot.Height);
            if(width<minimumWidth||height<minimumHeight)continue;
            var idealX=preferred=="right"?anchor.Right+gap:preferred=="left"?anchor.Left-gap-width:anchor.Left;
            var idealY=preferred=="top"?anchor.Top-gap-height:preferred=="bottom"?anchor.Bottom+gap:anchor.Top;
            var rect=new Rect(Math.Clamp(idealX,slot.Left,slot.Right-width),Math.Clamp(idealY,slot.Top,slot.Bottom-height),width,height);
            if(rect.IntersectsWith(anchor)||extra.Any(rect.IntersectsWith))continue;
            var dx=Math.Max(0,Math.Max(rect.Left-anchor.Right,anchor.Left-rect.Right));
            var dy=Math.Max(0,Math.Max(rect.Top-anchor.Bottom,anchor.Top-rect.Bottom));
            var sideName=rect.Left>=anchor.Right+gap-.5?"right":rect.Right<=anchor.Left-gap+.5?"left":rect.Bottom<=anchor.Top?"top":"bottom";
            var side=Array.IndexOf(new[]{preferred}.Concat(Sides).Distinct().ToArray(),sideName);
            candidates.Add((rect,width>=desired.Width&&height>=desired.Height,side,dx*dx+dy*dy,Math.Abs(rect.Top-anchor.Top),width*height));
        }
        return candidates.Count==0?null:candidates.OrderByDescending(c=>c.Full).ThenBy(c=>c.Full?0:-c.Area).ThenBy(c=>c.Side).ThenBy(c=>c.Distance).ThenBy(c=>c.Alignment).First().Rect;
    }
    internal static void Verify(Dictionary<string,bool> checks)
    {
        var work=new Rect(0,0,1920,1080);var size=new Size(300,300);
        checks["left right and top docking search above, bottom searches below"]=Sides.All(side=>SearchSide(side)==(side=="bottom"?"bottom":"top"));
        var anchor=new Rect(600,400,400,200);
        foreach(var side in Sides)
        {
            var d=Dock(anchor,work,new(280,140),side);
            checks["dock chosen side "+side]=d is Rect r&&work.Contains(r)&&!r.IntersectsWith(anchor)&&(side switch{"right"=>r.Left==1008&&r.Top==400,"left"=>r.Right==592&&r.Top==400,"top"=>r.Bottom==392&&r.Left==600,_=>r.Top==608&&r.Left==600});
        }
        foreach(var side in new[]{"left","right"})
        {
            var aligned=true;
            foreach(var scale in new[]{1.0,1.25,1.5,2.0})
            {
                var a=new Rect(600*scale,400*scale,400*scale,230*scale);var w=new Rect(0,0,1920*scale,1080*scale);
                var dock=Dock(a,w,new(280*scale,140*scale),side,gap:8*scale,alignBottom:true);
                aligned&=dock is Rect r&&r.Bottom==a.Bottom&&!r.IntersectsWith(a)&&w.Contains(r);
            }
            checks["statistics bottom aligned at controlled DPIs "+side]=aligned;
        }
        checks["bottom alignment preserved on automatic side fallback"]=Dock(new(1640,400,280,230),work,new(280,140),"right",alignBottom:true) is Rect bf&&bf.Right==1632&&bf.Bottom==630;
        checks["top and bottom docking unchanged by side alignment flag"]=Dock(anchor,work,new(280,140),"top",alignBottom:true)==Dock(anchor,work,new(280,140),"top")&&Dock(anchor,work,new(280,140),"bottom",alignBottom:true)==Dock(anchor,work,new(280,140),"bottom");
        checks["bottom aligned statistics still respect work area"]=Dock(new(600,950,400,200),work,new(280,140),"right",alignBottom:true) is Rect br&&work.Contains(br);
        checks["top search aligns with statistics"]=Choose(anchor,work,new(300,48),preferred:"top") is Rect t&&t.Left==anchor.Left&&t.Bottom==anchor.Top-8;
        checks["screen edge automatically chooses another side"]=Dock(new(1640,400,280,140),work,new(280,140),"right") is Rect edge&&edge.Right<=1632;
        checks["no free side hides instead of covering game"]=Dock(work,work,new(280,140),"right") is null;
        checks["activity exclusion switches side"]=Dock(anchor,work,new(280,140),"right",[new(1000,390,300,220)]) is Rect safe&&safe.Right<=anchor.Left-8;
        var right=Choose(new(500,200,360,255),work,size)!.Value;
        checks["popup opens beside right, not over statistics"]=right.X>=868&&!right.IntersectsWith(new(500,200,360,255));
        var left=Choose(new(1500,200,360,255),work,size)!.Value;
        checks["right edge switches to left"]=left.Right<=1492;
        var negative=Choose(new(-500,300,360,255),new(-1920,0,1920,1080),size)!.Value;
        checks["negative monitor coordinates remain valid"]=new Rect(-1920,0,1920,1080).Contains(negative)&&!negative.IntersectsWith(new(-500,300,360,255));
        var below=Choose(new(80,100,360,255),new(0,0,520,800),size)!.Value;
        checks["narrow screen uses below without overlap"]=below.Top>=363;
        checks["no room refuses overlap"]=Choose(new(0,0,360,255),new(0,0,360,255),size) is null;
        var valid=true;
        foreach(var scale in new[]{1.0,1.25,1.5,2.0})
            foreach(var x in new[]{0,300,900,1550})
                foreach(var y in new[]{0,300,800})
                {
                    var a=new Rect(x*scale,y*scale,360*scale,255*scale);var w=new Rect(0,0,1920*scale,1080*scale);
                    var p=Choose(a,w,new(660*scale,620*scale),8*scale,160*scale,36*scale);
                    valid&=p is null||(w.Contains(p.Value)&&!p.Value.IntersectsWith(a));
                }
        checks["all controlled geometry and DPI cases avoid statistics"]=valid;
        var stats=new Rect(42,45,360,255);var game=new Rect(415,90,340,140);
        var screenshot=Choose(stats,work,new(300,48),excluded:[game]);
        checks["user screenshot search avoids game and statistics"]=screenshot is Rect s&&!s.IntersectsWith(stats)&&!s.IntersectsWith(game);
        var every=true;
        foreach(var scale in new[]{1.0,1.25,1.5,2.0})
            foreach(var requested in new[]{new Size(300,48),new Size(300,310),new Size(660,620)})
            {
                var a=new Rect(42*scale,45*scale,360*scale,255*scale);var g=new Rect(415*scale,90*scale,340*scale,140*scale);var w=new Rect(0,0,1920*scale,1080*scale);
                var p=Choose(a,w,new(requested.Width*scale,requested.Height*scale),8*scale,160*scale,36*scale,[g]);
                every&=p is Rect r&&w.Contains(r)&&!r.IntersectsWith(a)&&!r.IntersectsWith(g);
            }
        checks["search results and details also avoid game at controlled DPIs"]=every;
        checks["full-screen game refuses covering it"]=Choose(stats,work,new(300,48),excluded:[work]) is null;
        var log=new Rect(430,235,280,300);var withLog=Choose(stats,work,new(300,310),excluded:[game,log]);
        checks["verified activity window also excluded"]=withLog is Rect l&&!l.IntersectsWith(game)&&!l.IntersectsWith(log)&&!l.IntersectsWith(stats);
        var rightPreference=Choose(new(400,400,280,140),work,new(300,48),excluded:[new Rect(700,420,340,140)]);
        checks["requested right-side preference is preserved safely"]=rightPreference is Rect rp&&rp.Left>=688&&!rp.IntersectsWith(new(700,420,340,140));
    }
}
