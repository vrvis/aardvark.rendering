﻿namespace Aardvark.Rendering.Vulkan.Raytracing

open Aardvark.Base
open Aardvark.Base.Incremental

open System
open System.Collections.Generic
open System.Runtime.InteropServices

type private AttributeWriter(input : IMod, index : int) =
    inherit AdaptiveObject()

    let size =
        match input.GetType() with
        | ModOf t -> Marshal.SizeOf t
        | t -> failwithf "[AttributeBuffer] unexpected input-type %A" t

    member x.Write(token : AdaptiveToken, buffer : byte[]) =
        x.EvaluateIfNeeded token () (fun token ->
            let value = input.GetValue token

            pinned buffer (fun _ ->
                pinned value (fun pSrc ->
                    let pDst = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, index * size)
                    Marshal.Copy(pSrc, pDst, size)
                )
            )
        )

    member x.Release() =
        lock x (fun _ ->
            input.RemoveOutput x
            x.Outputs.Clear()
        )

    /// Size of the attribute in bytes
    member x.Size =
        size

type AttributeArray(name : Symbol) =

    // CPU buffer
    let mutable data : byte[] = Array.empty

    // Size of the attribute in bytes
    let mutable attributeSize = None

    // Writers tracking the trace objects
    let writers = Dictionary<TraceObject, AttributeWriter>()

    // Sets the attribute size and checks for consistency
    let setAttributeSize (size : int) =
        match attributeSize with
        | Some x when x <> size ->
            failwithf "Encountered inconsistent size for attribute '%A'" name
        | _ ->
            attributeSize <- Some size

    member x.ApplyChanges(token : AdaptiveToken, indices : IndexPool,
                            added : TraceObject seq, removed : TraceObject seq) =
        // Create writers for new objects
        for obj in added do
            match obj.Attributes.TryGetValue name with
            | true, attr ->
                let w = AttributeWriter(attr, indices.Get obj)
                writers.[obj] <- w
                setAttributeSize w.Size
            | _ ->
                Log.warn "Trace object missing attribute '%A'" name

        // Remove objects
        for obj in removed do
            match writers.TryGetValue obj with
            | true, w ->
                w.Release()
                writers.Remove obj |> ignore
            | _ -> ()

        // Resize the array
        match attributeSize with
            | None -> ()
            | Some size ->
                data <- data.Resized ((indices.GetMaximum() + 1) * size)

        // Update writers
        for w in writers.Values do
            w.Write(token, data)

    member x.Dispose() =
        for w in writers.Values do
            w.Release()

        writers.Clear()

    member x.Data =
        data

    interface IDisposable with
        member x.Dispose() = x.Dispose()

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AttributeArray =

    let create (name : Symbol) =
        new AttributeArray(name)

    let delete (array : AttributeArray) =
        array.Dispose()

    let applyChanges (token :AdaptiveToken) (indices : IndexPool)
                        (added : TraceObject seq) (removed : TraceObject seq) (array : AttributeArray) =
        array.ApplyChanges(token, indices, added, removed) 