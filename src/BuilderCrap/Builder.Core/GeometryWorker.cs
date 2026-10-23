using System.Diagnostics;
using System.Text;
using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>One bounded, disposable process per calculation. No kernel state crosses jobs.</summary>
internal sealed class GeometryWorker(string executable)
{
    private const int Magic = 0x42574b31, ProtocolVersion = 8;
    private sealed record SimplifyMetadata(double Bound,int Boundaries,int Cavities,double Tolerance,double BeforeVolume,double AfterVolume);
    public Mesh?[] Calculate(GeometryRequest request, SceneObject[] inputs, CancellationToken cancellation,
        IProgress<OperationProgress>? progress)
        => CalculateInternal(request,inputs,cancellation,progress,out _,out _,out _,out _,out _,out _);
    public HollowKernelResult Hollow(HollowRequest request,SceneObject input,CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        var meshes=CalculateInternal(request,[input],cancellation,progress,out var radius,out var cavities,out _,out _,out _,out _);
        return new(meshes.Single()??throw new ModelException("The requested wall leaves no hollow cavity."),radius,cavities);
    }
    public SimplifyKernelResult Simplify(SimplifyRequest request,SceneObject input,CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        var meshes=CalculateInternal(request,[input],cancellation,progress,out _,out _,out var metadata,out _,out _,out _);
        var m=metadata??throw new IOException("Missing simplification verification metadata.");
        return new(meshes.Single()??throw new ModelException("Simplification produced no usable solid."),m.Bound,m.Boundaries,m.Cavities,m.Tolerance,m.BeforeVolume,m.AfterVolume);
    }
    public SmoothKernelResult Smooth(SmoothRequest request,SceneObject input,CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        _=CalculateInternal(request,[input],cancellation,progress,out _,out _,out _,out var result,out _,out _);
        return result??throw new IOException("Missing smoothing verification metadata.");
    }
    public ImmutableArray<SeparateKernelPiece> Separate(SeparateRequest request,SceneObject input,CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        _=CalculateInternal(request,[input],cancellation,progress,out _,out _,out _,out _,out var result,out _);
        return result;
    }
    public ExtrudeDownKernelResult ExtrudeDown(ExtrudeDownRequest request,SceneObject input,CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        _=CalculateInternal(request,[input],cancellation,progress,out _,out _,out _,out _,out _,out var result);
        return result??throw new IOException("Missing Extrude down material metadata.");
    }
    private Mesh?[] CalculateInternal(GeometryRequest request,SceneObject[] inputs,CancellationToken cancellation,
        IProgress<OperationProgress>? progress,out double maximumKernelRadius,out int cavityCount,out SimplifyMetadata? simplification,out SmoothKernelResult? smoothing,out ImmutableArray<SeparateKernelPiece> separation,out ExtrudeDownKernelResult? extrusion)
    {
        maximumKernelRadius=0;cavityCount=0;simplification=null;smoothing=null;separation=[];extrusion=null;
        if(!File.Exists(executable)) throw new ModelException("The geometry worker is missing. Run scripts/Build-GeometryWorker.ps1 and rebuild Builder.");
        // Files live in a unique scratch directory. User model paths never enter the worker protocol.
        var directory = Directory.CreateTempSubdirectory("Builder-geometry-");
        try
        {
            var input = Path.Combine(directory.FullName,"input.bin");
            var output = Path.Combine(directory.FullName,"output.bin");
            progress?.Report(new("Preparing geometry")); cancellation.ThrowIfCancellationRequested();
            using(var writer = new BinaryWriter(File.Create(input)))
            {
                writer.Write(Magic); writer.Write(ProtocolVersion);
                writer.Write(request switch { BooleanRequest b => (int)b.Kind, SplitRequest => 3, ValidateSolidRequest => 4,
                    TextSolidRequest t => t.Mode is null ? 5 : t.Mode==EmbossMode.Raised ? 6 : 7,
                    HollowRequest => 8,
                    SimplifyRequest => 9,
                    SmoothRequest => 10,
                    SeparateRequest => 11,
                    ExtrudeDownRequest => 12,
                    _ => throw new ModelException("Unsupported geometry operation.") });
                var plane = request as SplitRequest;
                var n = plane?.Normal ?? new Vec3(0,0,1);
                // Scale first to avoid overflow for otherwise valid finite normals.
                var scale = Math.Max(Math.Abs(n.X),Math.Max(Math.Abs(n.Y),Math.Abs(n.Z)));
                n = new(n.X/scale,n.Y/scale,n.Z/scale);
                var length = Math.Sqrt(n.X*n.X+n.Y*n.Y+n.Z*n.Z); n *= 1/length;
                writer.Write(n.X); writer.Write(n.Y); writer.Write(n.Z); writer.Write(plane?.OffsetMillimeters ?? 0);
                writer.Write(inputs.Length);
                foreach(var obj in inputs)
                {
                    var mesh = SolidInput(obj,cancellation);
                    writer.Write(mesh.Vertices.Length); writer.Write(mesh.Triangles.Length);
                    foreach(var v in mesh.Vertices) { cancellation.ThrowIfCancellationRequested(); writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
                    foreach(var t in mesh.Triangles) { cancellation.ThrowIfCancellationRequested(); writer.Write(t.A); writer.Write(t.B); writer.Write(t.C); }
                }
                if(request is TextSolidRequest text)
                {
                    writer.Write(text.DepthMillimeters); writer.Write(text.OverlapMillimeters);
                    foreach(var v in new[]{text.Placement.X,text.Placement.Y,text.Placement.Z,text.Placement.Offset})
                    { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
                    writer.Write(text.Outline.Contours.Length);
                    foreach(var contour in text.Outline.Contours)
                    {
                        cancellation.ThrowIfCancellationRequested();writer.Write(contour.Length);
                        foreach(var p in contour){writer.Write(p.X);writer.Write(p.Y);}
                    }
                }
                if(request is HollowRequest hollow)writer.Write(hollow.WallThicknessMillimeters);
                if(request is SimplifyRequest simplify){writer.Write(simplify.ReductionPercent);writer.Write(simplify.MaximumDeviationMillimeters);}
                if(request is SmoothRequest smooth){writer.Write(smooth.StrengthPercent);writer.Write(smooth.MaximumDeviationMillimeters);writer.Write(smooth.PreserveSharpEdges?1:0);}
                if(request is ExtrudeDownRequest extrudeDown)writer.Write(extrudeDown.PlaneHeightMillimeters);
            }
            cancellation.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true } };
            process.StartInfo.ArgumentList.Add(input); process.StartInfo.ArgumentList.Add(output);
            try
            {
                if(!process.Start()) throw new IOException("Could not start the geometry worker.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation,timeout.Token);
                using var registration = stop.Token.Register(() => Stop(process));
                progress?.Report(new("Calculating geometry"));
                process.WaitForExit();
                cancellation.ThrowIfCancellationRequested();
                if(timeout.IsCancellationRequested) throw new IOException("Geometry calculation exceeded two minutes. Try simpler operands.");
                if(process.ExitCode != 0 || !File.Exists(output)) throw new IOException("The geometry worker could not produce a result.");
            }
            catch(System.ComponentModel.Win32Exception e) { throw new IOException("Could not start the geometry worker: " + e.Message,e); }
            finally { Stop(process); }
            if(new FileInfo(output).Length > ModelLimits.MaxFileBytes) throw new IOException("Geometry result exceeds the transfer limit.");
            progress?.Report(new("Reading geometry result")); cancellation.ThrowIfCancellationRequested();
            using var reader = new BinaryReader(File.OpenRead(output));
            if(reader.ReadInt32() != Magic || reader.ReadInt32() != ProtocolVersion) throw new IOException("Geometry worker version mismatch.");
            var status = reader.ReadInt32(); var messageLength = reader.ReadInt32();
            if(messageLength < 0 || messageLength > 4096) throw new IOException("Invalid geometry response.");
            var messageBytes = reader.ReadBytes(messageLength);
            if(messageBytes.Length != messageLength) throw new IOException("Incomplete geometry response.");
            var message = Encoding.UTF8.GetString(messageBytes);
            if(status == 1) throw new ModelException(message);
            if(status != 0) throw new IOException(message);
            var count = reader.ReadInt32();
            if(count != (request is SeparateRequest?0:request is SplitRequest ? 2 : 1)) throw new IOException("Invalid geometry result count.");
            var meshes = new Mesh?[count];
            for(var i=0;i<count;i++)
            {
                var nv = reader.ReadInt32(); var nt = reader.ReadInt32();
                if(nv == 0 && nt == 0) continue;
                if(nv < 1 || nv > ModelLimits.MaxVertices || nt < 1 || nt > ModelLimits.MaxTriangles) throw new ModelException("The result exceeds geometry limits.");
                var vertices = new Vec3[nv]; var triangles = new Triangle[nt];
                for(var j=0;j<nv;j++) { cancellation.ThrowIfCancellationRequested(); vertices[j] = new(reader.ReadDouble(),reader.ReadDouble(),reader.ReadDouble()); }
                for(var j=0;j<nt;j++) { cancellation.ThrowIfCancellationRequested(); triangles[j] = new(reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32()); }
                meshes[i] = new(vertices,triangles);
                if(!Closed(meshes[i]!,cancellation)) throw new IOException("The geometry result is not a closed solid.");
            }
            if(request is HollowRequest h)
            {
                maximumKernelRadius=reader.ReadDouble();cavityCount=reader.ReadInt32();
                if(!double.IsFinite(maximumKernelRadius)||maximumKernelRadius<h.WallThicknessMillimeters||maximumKernelRadius>h.WallThicknessMillimeters*1.03||cavityCount<1||cavityCount>ModelLimits.MaxTriangles)
                    throw new IOException("Invalid hollow approximation metadata.");
            }
            if(request is SimplifyRequest s)
            {
                var bound=reader.ReadDouble();var boundaries=reader.ReadInt32();var cavities=reader.ReadInt32();
                var tolerance=reader.ReadDouble();var beforeVolume=reader.ReadDouble();var afterVolume=reader.ReadDouble();
                if(!double.IsFinite(bound)||bound<0||bound>s.MaximumDeviationMillimeters||boundaries<1||boundaries>ModelLimits.MaxTriangles||cavities<0||cavities>=boundaries||
                    !double.IsFinite(tolerance)||tolerance<0||tolerance>s.MaximumDeviationMillimeters||!double.IsFinite(beforeVolume)||beforeVolume<=0||!double.IsFinite(afterVolume)||afterVolume<=0)
                    throw new IOException("Invalid simplification verification metadata.");
                simplification=new(bound,boundaries,cavities,tolerance,beforeVolume,afterVolume);
            }
            if(request is SmoothRequest sm)
            {
                var motion=reader.ReadDouble();var bound=reader.ReadDouble();var fixedCount=reader.ReadInt32();var clipped=reader.ReadInt32();
                var boundaries=reader.ReadInt32();var cavities=reader.ReadInt32();var before=reader.ReadDouble();var after=reader.ReadDouble();
                var mesh=meshes.Single()??throw new IOException("Smoothing returned no mesh.");
                if(!double.IsFinite(motion)||motion<=0||!double.IsFinite(bound)||bound<motion||bound>sm.MaximumDeviationMillimeters||
                    fixedCount<0||fixedCount>mesh.Vertices.Length||clipped<0||clipped>mesh.Vertices.Length||boundaries<1||cavities<0||cavities>=boundaries||
                    !double.IsFinite(before)||before<=0||!double.IsFinite(after)||after<=0||mesh.Triangles.Length!=inputs[0].Mesh.Triangles.Length)
                    throw new IOException("Invalid smoothing verification metadata.");
                smoothing=new(mesh,motion,bound,fixedCount,clipped,boundaries,cavities,before,after);
            }
            if(request is SeparateRequest)
            {
                var pieceCount=reader.ReadInt32();if(pieceCount<2||pieceCount>ModelLimits.MaxObjects)throw new ModelException("Separation requires 2–1,000 material pieces.");
                var seen=new bool[inputs[0].Mesh.Triangles.Length];var previousFirst=-1;var pieces=ImmutableArray.CreateBuilder<SeparateKernelPiece>();
                for(var p=0;p<pieceCount;p++)
                {
                    var n=reader.ReadInt32();var boundaries=reader.ReadInt32();var cavities=reader.ReadInt32();var volume=reader.ReadDouble();
                    if(n<1||n>seen.Length||boundaries<1||boundaries>n||cavities<0||cavities!=boundaries-1||!double.IsFinite(volume)||volume<=0)
                        throw new IOException("Invalid separation piece metadata.");
                    var indices=ImmutableArray.CreateBuilder<int>();var previousIndex=-1;
                    for(var i=0;i<n;i++){cancellation.ThrowIfCancellationRequested();var index=reader.ReadInt32();if(index<0||index>=seen.Length||seen[index]||index<=previousIndex)throw new IOException("Invalid separation triangle partition.");seen[index]=true;previousIndex=index;indices.Add(index);}
                    if(indices[0]<=previousFirst)throw new IOException("Invalid separation piece ordering.");previousFirst=indices[0];pieces.Add(new(indices.ToImmutable(),boundaries,cavities,volume));
                }
                if(seen.Any(v=>!v))throw new IOException("The separation partition omitted source triangles.");separation=pieces.ToImmutable();
            }
            if(request is ExtrudeDownRequest)
            {
                var before=reader.ReadDouble();var after=reader.ReadDouble();var added=reader.ReadDouble();
                if(!double.IsFinite(before)||before<=0||!double.IsFinite(after)||after<=0||!double.IsFinite(added)||added<=0)
                    throw new IOException("Invalid Extrude down material metadata.");
                extrusion=new(meshes.Single()??throw new IOException("Extrude down returned no solid."),before,after,added);
            }
            if(reader.BaseStream.Position != reader.BaseStream.Length) throw new IOException("Unexpected geometry response data.");
            return meshes;
        }
        finally
        {
            // This directory was allocated by this invocation, and contains only its two protocol files.
            try { directory.Delete(true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }
    private static void Stop(Process process)
    {
        try { if(!process.HasExited) { process.Kill(entireProcessTree:true); process.WaitForExit(); } }
        catch(InvalidOperationException) { } // Never started, or already reaped.
        catch(System.ComponentModel.Win32Exception) when(process.HasExited) { }
    }
    private static Mesh SolidInput(SceneObject obj,CancellationToken cancellation)
    {
        var determinant = ModelFiles.Determinant(obj.Transform);
        var vertices = obj.Mesh.Vertices.Select(obj.Transform.Apply).ToArray();
        var triangles = obj.Mesh.Triangles.Select(t => determinant < 0 ? new Triangle(t.A,t.C,t.B) : t).ToArray();
        var world = new Mesh(vertices,triangles);
        if(Closed(world,cancellation)) return world;
        // STL repeats vertices for every face. Weld exact positions only, without moving or repairing geometry.
        var unique = new List<Vec3>(); var indices = new Dictionary<Vec3,int>(); var remap = new int[vertices.Length];
        for(var i=0;i<vertices.Length;i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!indices.TryGetValue(vertices[i],out var index)) { index = unique.Count; indices.Add(vertices[i],index); unique.Add(vertices[i]); }
            remap[i] = index;
        }
        world = new(unique,triangles.Select(t => new Triangle(remap[t.A],remap[t.B],remap[t.C])));
        if(!Closed(world,cancellation)) throw new ModelException($"{obj.Name}: use a closed solid with consistent face directions and no duplicate or zero-area faces. Use Repair to check this object first.");
        return world;
    }
    private static bool Closed(Mesh mesh,CancellationToken cancellation)
    {
        var edges = new HashSet<(int,int)>(); double volume6 = 0;
        var origin = mesh.Vertices[0];
        foreach(var t in mesh.Triangles)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = mesh.Vertices[t.A]-origin; var b = mesh.Vertices[t.B]-origin; var c = mesh.Vertices[t.C]-origin;
            var normal = Vec3.Cross(b-a,c-a);
            if(normal == new Vec3() || !edges.Add((t.A,t.B)) || !edges.Add((t.B,t.C)) || !edges.Add((t.C,t.A))) return false;
            var cross = Vec3.Cross(b,c); volume6 += a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z;
        }
        return volume6 > 0 && double.IsFinite(volume6) && edges.All(e => edges.Contains((e.Item2,e.Item1)));
    }
}
