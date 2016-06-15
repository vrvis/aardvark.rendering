﻿namespace Aardvark.Base

open System

type ResourceKind =
    | Unknown = 0
    | Buffer = 1
    | VertexArrayObject = 2
    | Texture = 3
    | UniformBuffer = 4
    | UniformLocation = 5
    | SamplerState = 6
    | ShaderProgram = 7
    | Renderbuffer = 8
    | Framebuffer = 9
    | IndirectBuffer = 10
    | DrawCall = 11
    | IndexBuffer = 12


module Map =
    let unionWith (f : Map<'k,'v>) (g : Map<'k,'v>) (fuse : 'v -> 'v -> 'v) (zero : 'v) =
        let mutable result = f
        for (k,right) in g |> Map.toSeq do
            let newValue = 
                match Map.tryFind k result with
                    | Some v -> fuse v right
                    | None -> fuse zero right

            result <- result |> Map.add k newValue
        result

[<StructuredFormatDisplay("{AsString}")>]
type MicroTime =
    struct
        val mutable public TotalNanoseconds : int64

        member x.TotalSeconds = float x.TotalNanoseconds / 1000000000.0
        member x.TotalMilliseconds = float x.TotalNanoseconds / 1000000.0
        member x.TotalMicroseconds = float x.TotalNanoseconds / 1000.0

        member private x.AsString =
            x.ToString()

        override x.ToString() =
            if x.TotalNanoseconds = 0L then "0"
            elif x.TotalNanoseconds > 1000000000L then sprintf "%.3fs" x.TotalSeconds
            elif x.TotalNanoseconds > 1000000L then sprintf "%.2fms" x.TotalMilliseconds
            elif x.TotalNanoseconds > 1000L then sprintf "%.1fµs" x.TotalMicroseconds
            else sprintf "%dns" x.TotalNanoseconds


        static member (+) (l : MicroTime, r : MicroTime) = MicroTime(l.TotalNanoseconds + r.TotalNanoseconds)
        static member (-) (l : MicroTime, r : MicroTime) = MicroTime(l.TotalNanoseconds - r.TotalNanoseconds)
        static member (~-) (l : MicroTime) = MicroTime(-l.TotalNanoseconds)
        static member (*) (l : MicroTime, r : float) = MicroTime(float l.TotalNanoseconds * r |> int64)
        static member (*) (l : float, r : MicroTime) = MicroTime(l * float r.TotalNanoseconds |> int64)
        static member (*) (l : MicroTime, r : int) = MicroTime(l.TotalNanoseconds * int64 r)
        static member (*) (l : int, r : MicroTime) = MicroTime(int64 l * r.TotalNanoseconds)
        static member (/) (l : MicroTime, r : int) = MicroTime(l.TotalNanoseconds / int64 r)
        static member (/) (l : MicroTime, r : float) = MicroTime(float l.TotalNanoseconds / r |> int64)

        static member (/) (l : MicroTime, r : MicroTime) = float l.TotalNanoseconds / float r.TotalNanoseconds


        static member Zero = MicroTime(0L)


        new (ns : int64) = { TotalNanoseconds = ns }
        new (ts : TimeSpan) = { TotalNanoseconds = (ts.Ticks * 1000000000L) / TimeSpan.TicksPerSecond}
    end

[<StructuredFormatDisplay("{AsString}")>]
type Mem =
    struct
        val mutable public Bytes : int64

        member x.Kilobytes = float x.Bytes / 1024.0
        member x.Megabytes = float x.Bytes / 1048576.0
        member x.Gigabytes = float x.Bytes / 1073741824.0
        member x.Terabytes = float x.Bytes / 1099511627776.0

        member private x.AsString = x.ToString()

        override x.ToString() =
            let b = abs x.Bytes
            if b = 0L then "0"
            elif b > 1099511627776L then sprintf "%.3fTB" x.Terabytes
            elif b > 1073741824L then sprintf "%.3fGB" x.Gigabytes
            elif b > 1048576L then sprintf "%.2fMB" x.Megabytes
            elif b > 1024L then sprintf "%.1fkB" x.Kilobytes
            else sprintf "%db" x.Bytes

        static member Zero = Mem(0L)

        static member (+) (l : Mem, r : Mem) = Mem(l.Bytes + r.Bytes)
        static member (-) (l : Mem, r : Mem) = Mem(l.Bytes - r.Bytes)
        static member (~-) (l : Mem) = Mem(-l.Bytes)

        static member (*) (l : Mem, r : int) = Mem(l.Bytes * int64 r)
        static member (*) (l : Mem, r : float) = Mem(float l.Bytes * r |> int64)
        static member (*) (l : int, r : Mem) = Mem(int64 l * r.Bytes)
        static member (*) (l : float, r : Mem) = Mem(l * float r.Bytes |> int64)
        static member (/) (l : Mem, r : int) = Mem(l.Bytes / int64 r)
        static member (/) (l : Mem, r : float) = Mem(float l.Bytes / r |> int64)
        static member (/) (l : Mem, r : Mem) = float l.Bytes / float r.Bytes

        new(bytes : int64) = { Bytes = bytes }
        new(bytes : int) = { Bytes = int64 bytes }
        new(bytes : uint64) = { Bytes = int64 bytes }
        new(bytes : uint32) = { Bytes = int64 bytes }
        new(bytes : nativeint) = { Bytes = int64 bytes }
        new(bytes : unativeint) = { Bytes = int64 bytes }
    end



type FrameStatistics =
    {
        /// the number of render passes executed
        RenderPassCount : float

        /// the number of instructions contained
        InstructionCount : float

        /// the number of issued driver instructions
        ActiveInstructionCount : float

        /// the number of calls to draw-instructions
        DrawCallCount : float

        /// the effective number of draw-calls (including indirect calls)
        EffectiveDrawCallCount : float

        /// the number of updated resources
        ResourceUpdateCount : float

        /// the number of resource-updates per ResourceKind
        ResourceUpdateCounts : Map<ResourceKind,float>

        /// the number of primitives rendered
        PrimitiveCount : float

        /// the time spent in sorting RenderObjects (view-depenent, etc.)
        SortingTime : MicroTime

        /// the time spent in program updates (compiling instructions)
        /// NOTE: includes the SortingTime
        ProgramUpdateTime : MicroTime

        /// the time spent in the submission of resource-update commands (CPU)
        ResourceUpdateSubmissionTime : MicroTime

        /// the time spent in resource-updates (GPU)
        ResourceUpdateTime : MicroTime

        /// the time spent in the submission of rendering-commands (CPU)
        SubmissionTime : MicroTime

        /// the time spent in rendering (GPU)
        ExecutionTime : MicroTime

        /// the total number of physical resources (IResource)
        PhysicalResourceCount : float

        /// the total number of resource-references
        VirtualResourceCount : float
        
        /// used resources by their kind
        ResourceCounts : Map<ResourceKind, float>
        
        // the total (approximate) memory-size for all resources
        ResourceSize : Mem

        /// the total number of added/removed renderobjects in one frame
        AddedRenderObjects : float
        RemovedRenderObjects : float

        // historical
        JumpDistance : float
        ProgramSize : uint64
    } with

    static member Zero =
        {
            RenderPassCount = 0.0
            InstructionCount = 0.0
            ActiveInstructionCount = 0.0
            DrawCallCount = 0.0
            EffectiveDrawCallCount = 0.0
            ResourceUpdateCount = 0.0
            PrimitiveCount = 0.0
            JumpDistance = 0.0
            VirtualResourceCount = 0.0
            PhysicalResourceCount = 0.0
            ResourceUpdateCounts = Map.empty
            AddedRenderObjects = 0.0
            RemovedRenderObjects = 0.0
            SortingTime = MicroTime.Zero
            ProgramUpdateTime = MicroTime.Zero
            ResourceUpdateSubmissionTime = MicroTime.Zero
            ResourceUpdateTime = MicroTime.Zero
            SubmissionTime = MicroTime.Zero
            ExecutionTime = MicroTime.Zero
            ProgramSize = 0UL
            ResourceCounts = Map.empty
            ResourceSize = Mem.Zero
        }

    static member DivideByInt(l : FrameStatistics, r : int) =
        l / float r

    static member (+) (l : FrameStatistics, r : FrameStatistics) =
        {
            RenderPassCount = l.RenderPassCount + r.RenderPassCount
            InstructionCount = l.InstructionCount + r.InstructionCount
            ActiveInstructionCount = l.ActiveInstructionCount + r.ActiveInstructionCount
            DrawCallCount = l.DrawCallCount + r.DrawCallCount
            EffectiveDrawCallCount = l.EffectiveDrawCallCount + r.EffectiveDrawCallCount
            ResourceUpdateCount = l.ResourceUpdateCount + r.ResourceUpdateCount
            PrimitiveCount = l.PrimitiveCount + r.PrimitiveCount
            JumpDistance = l.JumpDistance + r.JumpDistance
            VirtualResourceCount = l.VirtualResourceCount + r.VirtualResourceCount
            PhysicalResourceCount = l.PhysicalResourceCount + r.PhysicalResourceCount
            ResourceUpdateCounts = Map.unionWith l.ResourceUpdateCounts r.ResourceUpdateCounts (+) 0.0
            ResourceCounts = Map.unionWith l.ResourceCounts r.ResourceCounts (+) 0.0
            AddedRenderObjects = l.AddedRenderObjects + r.AddedRenderObjects
            RemovedRenderObjects = l.RemovedRenderObjects + r.RemovedRenderObjects
            SortingTime = l.SortingTime + r.SortingTime
            ProgramUpdateTime = l.ProgramUpdateTime + r.ProgramUpdateTime
            ResourceUpdateSubmissionTime = l.ResourceUpdateSubmissionTime + r.ResourceUpdateSubmissionTime
            ResourceUpdateTime = l.ResourceUpdateTime + r.ResourceUpdateTime
            SubmissionTime = l.SubmissionTime + r.SubmissionTime
            ExecutionTime = l.ExecutionTime + r.ExecutionTime
            ProgramSize = l.ProgramSize + r.ProgramSize
            ResourceSize = l.ResourceSize + r.ResourceSize
        }

    static member (-) (l : FrameStatistics, r : FrameStatistics) =
        {
            RenderPassCount = l.RenderPassCount - r.RenderPassCount
            InstructionCount = l.InstructionCount - r.InstructionCount
            ActiveInstructionCount = l.ActiveInstructionCount - r.ActiveInstructionCount
            DrawCallCount = l.DrawCallCount - r.DrawCallCount
            EffectiveDrawCallCount = l.EffectiveDrawCallCount - r.EffectiveDrawCallCount
            ResourceUpdateCount = l.ResourceUpdateCount - r.ResourceUpdateCount
            PrimitiveCount = l.PrimitiveCount - r.PrimitiveCount
            JumpDistance = l.JumpDistance - r.JumpDistance
            VirtualResourceCount = l.VirtualResourceCount - r.VirtualResourceCount
            PhysicalResourceCount = l.PhysicalResourceCount - r.PhysicalResourceCount
            ResourceUpdateCounts = Map.unionWith l.ResourceUpdateCounts r.ResourceUpdateCounts (-) 0.0
            ResourceCounts = Map.unionWith l.ResourceCounts r.ResourceCounts (-) 0.0
            AddedRenderObjects = l.AddedRenderObjects - r.AddedRenderObjects
            RemovedRenderObjects = l.RemovedRenderObjects - r.RemovedRenderObjects
            SortingTime = l.SortingTime - r.SortingTime
            ProgramUpdateTime = l.ProgramUpdateTime - r.ProgramUpdateTime
            ResourceUpdateSubmissionTime = l.ResourceUpdateSubmissionTime - r.ResourceUpdateSubmissionTime
            ResourceUpdateTime = l.ResourceUpdateTime - r.ResourceUpdateTime
            SubmissionTime = l.SubmissionTime - r.SubmissionTime
            ExecutionTime = l.ExecutionTime - r.ExecutionTime
            ProgramSize = l.ProgramSize - r.ProgramSize
            ResourceSize = l.ResourceSize - r.ResourceSize
        }

    static member (/) (l : FrameStatistics, r : float) =
        {
            RenderPassCount = l.RenderPassCount / r
            InstructionCount = l.InstructionCount / r
            ActiveInstructionCount = l.ActiveInstructionCount / r
            DrawCallCount = l.DrawCallCount / r
            EffectiveDrawCallCount = l.EffectiveDrawCallCount / r
            ResourceUpdateCount = l.ResourceUpdateCount / r
            PrimitiveCount = l.PrimitiveCount / r
            JumpDistance = l.JumpDistance / r
            VirtualResourceCount = l.VirtualResourceCount / r
            PhysicalResourceCount = l.PhysicalResourceCount / r
            ResourceUpdateCounts = Map.map (fun k v -> v / r) l.ResourceUpdateCounts
            ResourceCounts = Map.map (fun k v -> v / r) l.ResourceCounts
            AddedRenderObjects = l.AddedRenderObjects / r
            RemovedRenderObjects = l.RemovedRenderObjects / r
            SortingTime = l.SortingTime / r
            ProgramUpdateTime = l.ProgramUpdateTime / r
            ResourceUpdateSubmissionTime = l.ResourceUpdateSubmissionTime / r
            ResourceUpdateTime = l.ResourceUpdateTime / r
            SubmissionTime = l.SubmissionTime / r
            ExecutionTime = l.ExecutionTime / r
            ProgramSize = uint64 (float l.ProgramSize / r)
            ResourceSize = l.ResourceSize / r
        }

