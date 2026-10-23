using Builder.Core;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? pendingPick;
    private long pickGeneration,cameraGeneration;
    private sealed record ViewportPick(Guid? ObjectId,long Revision,Guid? EditingGroupId,bool IsCurrentCapture);

    // Camera projection stays in this adapter. Core receives only a world ray and clip distances.
    private async Task<ViewportPick> QueryViewportPick(Point point,CancellationToken cancellation)
    {
        var snapshot=editor.Snapshot;
        if(!TryViewportRay(point,out var origin,out var direction,out var near,out var far))
            return new(null,snapshot.Revision,snapshot.EditingGroupId,false);
        var hit=await editor.PickAsync(origin,direction,near,far,cancellation);
        var sameCapture=hit==null || hit.CapturedRevision==snapshot.Revision && hit.EditingGroupId==snapshot.EditingGroupId;
        return new(hit?.ObjectId,snapshot.Revision,snapshot.EditingGroupId,sameCapture);
    }

    private async Task<Guid?> PickViewportObject(Point point)
        => (await QueryViewportPick(point,CancellationToken.None)).ObjectId;

    private async Task SelectViewportAt(Point point,ModifierKeys modifiers)
    {
        CancelPendingPick();
        using var cancellation=new CancellationTokenSource(); pendingPick=cancellation;
        var generation=pickGeneration; var camera=cameraGeneration;
        try
        {
            var picked=await QueryViewportPick(point,cancellation.Token);
            var current=editor.Snapshot;
            // A stale/cancelled query is not a miss. It must never clear a newer selection.
            if(cancellation.IsCancellationRequested || !picked.IsCurrentCapture || generation!=pickGeneration || camera!=cameraGeneration ||
                current.Revision!=picked.Revision || current.EditingGroupId!=picked.EditingGroupId ||
                ModalToolActive || operation!=null || constructionMode!=null || handleMode!=null || arrangementMode!=null) return;
            SelectViewportObjectAtRevision(picked.ObjectId,modifiers,picked.Revision);
            Refresh();
        }
        catch(OperationCanceledException) { }
        catch(ModelException e) { Message(e.Message,true); }
        finally { if(ReferenceEquals(pendingPick,cancellation)) pendingPick=null; }
    }

    private void CancelPendingPick()
    { pickGeneration++; pendingPick?.Cancel(); }

    private bool TryViewportRay(Point point,out Vec3 origin,out Vec3 direction,out double minimum,out double maximum)
    {
        origin=direction=new(); minimum=maximum=0;
        var projection=TransformGizmo.Projection.Capture(Viewport.Camera as ProjectionCamera,new(Viewport.ActualWidth,Viewport.ActualHeight));
        return projection!=null && projection.TryRay(point,out origin,out direction,out minimum,out maximum);
    }
}
