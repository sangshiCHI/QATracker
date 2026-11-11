namespace Companion.Diagnostics;

// The game's native activity window already follows its DOM scene during a drag.
// Use that observed translation, never extrapolated mouse motion; UIA remains the baseline.
internal sealed class NativeFollowerMotion
{
    Box? bodyAnchor,proxyAnchor,lastBody,lastProxy;
    bool wasDown,armed,active;double stableSince,releasedAt;
    public bool Active=>active;
    public Box Resolve(Box body,Box proxy,Box monitor,int mouseX,int mouseY,bool down,bool pointerTargetsScene,double now)
    {
        var previousProxy=lastProxy;
        var edge=proxy.X<=monitor.X+1||proxy.Y<=monitor.Y+1||
            proxy.X+proxy.Width>=monitor.X+monitor.Width-1||proxy.Y+proxy.Height>=monitor.Y+monitor.Height-1;
        var sizeChanged=lastBody is not null&&(body.Width!=lastBody.Width||body.Height!=lastBody.Height)||
            lastProxy is not null&&(proxy.Width!=lastProxy.Width||proxy.Height!=lastProxy.Height);
        if(sizeChanged){bodyAnchor=null;proxyAnchor=null;armed=active=false;}
        if(body!=lastBody||proxy!=lastProxy)stableSince=now;
        lastBody=body;lastProxy=proxy;
        if(!down&&!wasDown&&!armed&&now-stableSince>=350&&!edge){bodyAnchor=body;proxyAnchor=proxy;}
        if(down&&!wasDown)
        {
            armed=pointerTargetsScene&&bodyAnchor is not null&&proxyAnchor is not null&&
                body==bodyAnchor&&previousProxy==proxyAnchor&&
                mouseX>=body.X&&mouseX<body.X+body.Width&&mouseY>=body.Y&&mouseY<body.Y+body.Height;
            active=false;
        }
        if(armed&&proxyAnchor is not null&&bodyAnchor is not null)
        {
            if(down&&(proxy.X!=proxyAnchor.X||proxy.Y!=proxyAnchor.Y))active=true;
            if(!down&&wasDown)releasedAt=now;
            var translated=bodyAnchor with{X=bodyAnchor.X+proxy.X-proxyAnchor.X,Y=bodyAnchor.Y+proxy.Y-proxyAnchor.Y};
            // A clamped follower no longer exposes the scene's full movement.
            if(!down&&body==translated){armed=active=false;bodyAnchor=body;proxyAnchor=proxy;}
            else if(!down&&now-releasedAt>600){armed=active=false;bodyAnchor=proxyAnchor=null;}
            wasDown=down;
            if(active&&!edge)return translated;
        }
        wasDown=down;return body;
    }
}
