using Builder.Core;
using Builder.Windows.Text;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static void BeginText()
    {
        var item=Tree(window).OfType<MenuItem>().Single(m=>m.Header?.ToString()=="New text…");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,item));
        Assert(Named<FrameworkElement>("TextToolPanel").Visibility==Visibility.Visible,"New text panel did not open.");
    }
    private static async Task PreviewText()
    {
        Click(Named<Button>("PreviewTextButton"));await Idle();
        Assert(editor.Snapshot.Preview?.Request is TextRequest or EmbossRequest,"Text preview failed: "+Named<TextBlock>("Status").Text);
    }
    private static async Task RunTextEmbossControls()
    {
        var provider=new WindowsFontOutlineProvider();var families=provider.GetFontFamilies();
        Assert(families.Contains("Arial")&&editor.GetFontFamilies().SequenceEqual(families),"Session font catalog differs from provider.");
        var outlines=new Dictionary<string,TextOutline>();
        foreach(var text in new[]{"O","B8","AV","A A","é","e\u0301","مرحبا","中文","😀"})
        {
            var outline=await provider.BuildAsync(new(text,"Arial",10));outlines[text]=outline;
            var points=outline.Contours.SelectMany(c=>c).ToArray();
            Near(points.Min(p=>p.X),0);Near(points.Min(p=>p.Y),0);Near(points.Max(p=>p.Y),10);
            Assert(outline.FillRule==TextFillRule.NonZero&&outline.Provenance!.Contains("sha256"),"Outline fill/provenance missing.");
        }
        Assert(outlines["O"].Contours.Length==2&&outlines["B8"].Contours.Length==6,"O/B8 counters were lost.");
        Assert(outlines["é"].Contours.SelectMany(c=>c).SequenceEqual(outlines["e\u0301"].Contours.SelectMany(c=>c)),"Composed/combining accent layout differs.");
        async Task Reject(TextOutlineSpec spec)
        {try{await provider.BuildAsync(spec);throw new InvalidOperationException("Unsupported typography was accepted.");}catch(ModelException){}}
        await Reject(new("O","Definitely not an installed font",10));await Reject(new("\u0378","Arial",10));await Reject(new("   ","Arial",10));
        using(var cancelled=new CancellationTokenSource())
        {cancelled.Cancel();try{await provider.BuildAsync(new("O","Arial",10),cancelled.Token);throw new InvalidOperationException("Cancelled provider returned contours.");}catch(OperationCanceledException){}}
        File.WriteAllText(Path.Combine(output,"installed-font-outline-probe.json"),JsonSerializer.Serialize(outlines,new JsonSerializerOptions{WriteIndented=true}));
        // Saved font-derived outputs let the independent verifier inspect actual holes/volume,
        // independently of our viewport and the provider's own polygon calculations.
        foreach(var text in new[]{"O","B8","é"})
        {
            var session=new EditorSession(textOutlineProvider:provider);
            Ok(await session.PreviewTextAsync(new(new FontTextSource(new(text,"Arial",10)),2)));
            Ok(session.ApplyPreview(session.Snapshot.Preview!.Id));
            var basename=text=="é"?"accent":text;
            Ok(await session.ExportStlAsync(Path.Combine(output,"font-"+basename+".stl")));
            Ok(await session.SaveAsync(Path.Combine(output,"font-"+basename+".3mf")));
        }
        observations.Add(new{fontFamilies=families.Count,outlineCases=outlines.Keys,physicalHeightMillimeters=10,flatteningToleranceMillimeters=.02,fillRule="NonZero; Y up; outer CCW, holes CW from WPF outlined region"});
        Pass("Window-independent installed typography preserves physical ink height, counters, shaping/fallback provenance and rejects missing fonts/glyphs with cancellation");

        await Open("cube-20mm.stl");var before=editor.Snapshot;
        BeginText();Assert(!Named<Button>("ApplyTextButton").IsEnabled,"Text Add enabled without review.");
        Named<TextBox>("TextContent").Text="O";Named<TextBox>("TextHeight").Text="10";Named<TextBox>("TextDepth").Text="2";
        InspectionFooterInside("PreviewTextButton","ApplyTextButton","CancelTextButton");Capture("new-text-minimum",1040,680);
        await PreviewText();SameAccepted(before);var candidate=editor.Snapshot.Preview!;var id=candidate.ResultObjectIds.Single();
        var solid=candidate.Scene.Objects.Single(o=>o.Id==id);Near(solid.Bounds.Size.Y,10);Near(solid.Bounds.Size.Z,2);Near(solid.Bounds.Min.X,0);Near(solid.Bounds.Min.Y,0);Near(solid.Bounds.Min.Z,0);
        var area=outlines["O"].Contours.Sum(c=>Enumerable.Range(0,c.Length).Sum(i=>c[i].X*c[(i+1)%c.Length].Y-c[(i+1)%c.Length].X*c[i].Y)/2);
        var signedVolume=solid.Mesh.Triangles.Sum(t=>{var a=solid.Mesh.Vertices[t.A];var b=solid.Mesh.Vertices[t.B];var c=solid.Mesh.Vertices[t.C];return (a.X*(b.Y*c.Z-b.Z*c.Y)+a.Y*(b.Z*c.X-b.X*c.Z)+a.Z*(b.X*c.Y-b.Y*c.X))/6;});
        Near(Math.Abs(signedVolume),area*2,.0001);
        Click(Named<Button>("ApplyTextButton"));Assert(editor.Snapshot.Scene.Objects.Length==before.Scene.Objects.Length+1&&editor.Snapshot.SelectedObjectId==id,"New text acceptance lost object/selection.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Text Undo did not restore accepted scene.");
        Click(Named<Button>("RedoButton"));Assert(editor.Snapshot.Scene.Objects.Any(o=>o.Id==id),"Text Redo lost stable ID.");Click(Named<Button>("UndoButton"));
        Pass("New text previews a physically sized holed solid, accepts only one stable object and restores exact scene with Undo/Redo");

        before=editor.Snapshot;BeginText();Named<TextBox>("TextContent").Text="O";await PreviewText();candidate=editor.Snapshot.Preview!;
        Named<TextBox>("TextContent").Text="\u0378";Assert(!Named<Button>("ApplyTextButton").IsEnabled,"Edited text left stale Add enabled.");
        Invoke("ApplyTextClick",window,new RoutedEventArgs());SameAccepted(before);
        Click(Named<Button>("PreviewTextButton"));await Idle();Assert(editor.Snapshot.Preview?.Id==candidate.Id,"Missing glyph discarded prior candidate.");
        Named<TextBox>("TextContent").Text="B8";await PreviewText();Assert(editor.Snapshot.Preview!.Id!=candidate.Id&&editor.Snapshot.Preview.ResultObjectIds.Single()==candidate.ResultObjectIds.Single(),"Replacement text did not retain candidate identity with fresh token.");
        Click(Named<Button>("CancelTextButton"));SameAccepted(before);Assert(editor.Snapshot.Preview==null,"Cancel kept text candidate.");
        BeginText();Click(Named<Button>("PreviewTextButton"));Click(Named<Button>("CancelTextButton"));await Idle();SameAccepted(before);
        Assert(Named<FrameworkElement>("TextToolActions").Visibility==Visibility.Collapsed,"In-flight cancellation kept text tool open.");
        Pass("Changed/missing-glyph text drafts cannot commit prior candidates; replacement, cancel and in-flight cancel preserve accepted state");

        BeginText();await PreviewText();
        foreach(var name in new[]{"TextContent","TextHeight","TextDepth","TextOffsetU","TextOffsetV","TextRotation"})
        {var field=Named<TextBox>(name);Assert(field.IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(field),"Text field lost normal local Undo/IME: "+name);}
        CheckLocalTextUndo(Named<TextBox>("TextContent"),"O","B8");SameAccepted(before);
        Assert(!Named<Button>("ApplyTextButton").IsEnabled,"Local text editing/Undo incorrectly re-enabled the stale preview.");
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-text-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewTextButton","ApplyTextButton","CancelTextButton"})
            {var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted text footer button.");SameAccepted(before);}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelTextButton").RaiseEvent(escape);Assert(escape.Handled,"Text Escape was not handled.");
        }
        SameAccepted(before);Pass("Text controls retain local text Undo/IME, standard footer Enter and Escape without changing accepted state");

        await Open("cube-20mm.stl");before=editor.Snapshot;var target=before.SelectedObjectId!.Value;
        Click(Named<Button>("EmbossButton"));Assert(Named<ComboBox>("TextFace").SelectedIndex==4&&Named<ComboBox>("TextEmbossMode").SelectedIndex==0,"Emboss defaults differ from raised/top face.");
        InspectionFooterInside("PreviewTextButton","ApplyTextButton","CancelTextButton");await PreviewText();SameAccepted(before);
        candidate=editor.Snapshot.Preview!;Near(candidate.Scene.Bounds.Max.Z,21);
        Assert(candidate.ResultObjectIds.SequenceEqual([target]),"Emboss changed object identity.");Capture("raised-text-minimum",1040,680);
        Named<TextBox>("TextOffsetU").Text="100";Assert(!Named<Button>("ApplyTextButton").IsEnabled,"Placement draft retained stale Apply.");Click(Named<Button>("PreviewTextButton"));await Idle();
        Assert(editor.Snapshot.Preview?.Id==candidate.Id,"Unsupported footprint discarded prior emboss candidate.");SameAccepted(before);
        Named<TextBox>("TextOffsetU").Text="0";Named<ComboBox>("TextEmbossMode").SelectedIndex=1;Named<TextBox>("TextRotation").Text="15";await PreviewText();
        Assert(((EmbossRequest)editor.Snapshot.Preview!.Request).Mode==EmbossMode.Engraved,"Emboss mode not routed to Core.");Near(editor.Snapshot.Preview.Scene.Bounds.Max.Z,20);
        Click(Named<Button>("ApplyTextButton"));Assert(editor.Snapshot.SelectedObjectId==target&&editor.Snapshot.Scene.Objects.Length==1,"Emboss acceptance changed target identity/count.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Emboss Undo lost exact scene.");Click(Named<Button>("RedoButton"));Click(Named<Button>("UndoButton"));
        Pass("Raised/engraved planar emboss uses shared preview, validates physical placement drafts and preserves target identity through Undo/Redo");

        await Open("assembly-mm.3mf");var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(await editor.PreviewArrangementAsync(new GroupRequest(ids)));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();
        Assert(!Named<Button>("EmbossButton").IsEnabled,"Whole group enabled single-target emboss.");var group=editor.Snapshot.Scene.Groups.Single();
        Ok(editor.EnterGroup(group.Id,ids[0]));Refresh();before=editor.Snapshot;var sibling=before.Scene.Objects.Single(o=>o.Id==ids[1]);
        Click(Named<Button>("EmbossButton"));await PreviewText();
        Assert(editor.Snapshot.Preview!.EditingGroupId==group.Id&&ReferenceEquals(editor.Snapshot.Preview.Scene.Objects.Single(o=>o.Id==ids[1]),sibling),"Scoped emboss touched siblings or lost scope.");
        Invoke("MeasureClick",window,new RoutedEventArgs());Invoke("MoveHandleClick",window,new RoutedEventArgs());Invoke("NewTextClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());SameAccepted(before);
        Assert(Named<FrameworkElement>("TextToolActions").Visibility==Visibility.Visible&&Named<FrameworkElement>("MeasurementActions").Visibility==Visibility.Collapsed,"Conflicting tool replaced scoped emboss.");
        Click(Named<Button>("ApplyTextButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single()==group,"Emboss lost group membership/scope.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Scoped emboss Undo failed.");
        Ok(editor.ExitGroup());Refresh();Pass("Emboss requires one independent target or scoped member, preserves siblings/membership and guards conflicting tools/history");
    }
}
