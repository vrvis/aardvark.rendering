﻿namespace Aardvark.Rendering.Vulkan.Raytracing

open Aardvark.Base
open System.Collections.Generic

type IndexPool() =

    // Next highest index
    let mutable nextIndex = 0

    // Indices that can be (re-)assigned
    let freeIndices = Queue<int>()

    // Current set of objects and assigned indices
    let indices = Dictionary<TraceObject, int>()

    // Removes objects and assigns indices to newly added ones
    member x.ApplyChanges(added : TraceObject seq, removed : TraceObject seq) =

        for k in removed do
            match indices.TryGetValue k with
            | true, i ->
                freeIndices.Enqueue i
                indices.Remove k |> ignore
            | _ -> ()

        for _ in 1 .. (Seq.length added - freeIndices.Count) do
            let i = nextIndex
            inc &nextIndex
            freeIndices.Enqueue i

        for k in added do
            indices.[k] <- freeIndices.Dequeue()

    /// Returns all objects currently present in the pool
    member x.Objects = indices.Keys :> TraceObject seq

    /// Returns the unique index of the given object
    member x.Get(obj : TraceObject) =
        indices.[obj]

    // Returns the highest index among current objects or -1 if there are no objects
    member x.GetMaximum() =
        if indices.IsEmpty() then
            -1
        else
            indices.Values |> Seq.max

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module IndexPool =

    let create () =
        new IndexPool()

    let applyChanges (added : TraceObject seq) (removed : TraceObject seq) (pool : IndexPool) =
        pool.ApplyChanges(added, removed) 