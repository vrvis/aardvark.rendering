﻿namespace Aardvark.Rendering.Vulkan

open System
open System.Threading
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Collections.Generic
open System.Collections.Concurrent
open Aardvark.Base
open Aardvark.Rendering.Vulkan


#nowarn "9"
#nowarn "51"

type SparseImageBind =
    {
        level   : int
        slice   : int
        offset  : V3i
        size    : V3i
        pointer : Option<DevicePtr>
    }

    static member Unbind(level : int, slice : int, offset : V3i, size : V3i) =
        {
            level = level
            slice = slice
            offset = offset
            size = size
            pointer = None
        }

    static member Bind(level : int, slice : int, offset : V3i, size : V3i, ptr : DevicePtr) =
        {
            level = level
            slice = slice
            offset = offset
            size = size
            pointer = Some ptr
        }


module private Align =
    let inline next (v : ^a) (a : ^b) =
        if v % a = LanguagePrimitives.GenericZero then v
        else v + (a - (v % a))

    let next3 (v : V3i) (a : V3i) =
        V3i(next v.X a.X, next v.Y a.Y, next v.Z a.Z)

module Helpers = 
    let rec mipSize (s : V3i) (pageSize : V3i) (levels : int) =
        if levels <= 0 then 
            0L
        else
            let sa = Align.next3 s pageSize
            int64 sa.X * int64 sa.Y * int64 sa.Z + mipSize (s / 2) pageSize (levels - 1)
        
type SparseImage(device : Device, handle : VkImage, size : V3i, levels : int, slices : int, dim : TextureDimension, format : VkFormat) =
    inherit Image(device, handle, size, levels, slices, 1, dim, format, DevicePtr.Null, VkImageLayout.Undefined)


    static let div (a : int) (b : int) =
        if a % b = 0 then a / b
        else 1 + a / b

    static let div3 (a : V3i) (b : V3i) =
        V3i(div a.X b.X, div a.Y b.Y, div a.Z b.Z)

    static let mod3 (a : V3i) (b : V3i) =
        V3i(a.X % b.X, a.Y % b.Y, a.Z % b.Z)

    let requirements =
        let mutable reqs = VkMemoryRequirements()
        VkRaw.vkGetImageMemoryRequirements(device.Handle, handle, &&reqs)
        reqs

    let memory =
        let mutable reqs = VkMemoryRequirements()
        VkRaw.vkGetImageMemoryRequirements(device.Handle, handle, &&reqs)

        device.Memories |> Array.find (fun mem ->
            let mask = 1u <<< mem.Index
            mask &&& reqs.memoryTypeBits <> 0u
        )

    let sparseRequirements =
        let mutable count = 0u
        VkRaw.vkGetImageSparseMemoryRequirements(device.Handle, handle, &&count, NativePtr.zero)

        let requirements = Array.zeroCreate (int count)
        requirements |> NativePtr.withA (fun pRequirements ->
            VkRaw.vkGetImageSparseMemoryRequirements(device.Handle, handle, &&count, pRequirements)
        )

        requirements

    let alwaysResident = List<DevicePtr>()

    let pageSizeInBytes = int64 requirements.alignment

    let pageSize = 
        let v = sparseRequirements.[0].formatProperties.imageGranularity
        V3i(int v.width, int v.height, int v.depth)

    let sparseLevels =
        int sparseRequirements.[0].imageMipTailFirstLod

    do 
        let binds = List<VkSparseMemoryBind>()


        for req in sparseRequirements do
            if req.formatProperties.aspectMask = VkImageAspectFlags.MetadataBit then
                if req.formatProperties.flags = VkSparseImageFormatFlags.SingleMiptailBit then
                    let mem = memory.Alloc(int64 requirements.alignment, int64 req.imageMipTailSize)
                    alwaysResident.Add mem

                    let bind = 
                        VkSparseMemoryBind(
                            req.imageMipTailOffset,
                            req.imageMipTailSize,
                            mem.Memory.Handle,
                            uint64 mem.Offset,
                            VkSparseMemoryBindFlags.MetadataBit
                        )

                    binds.Add bind
                else
                    let mutable targetOffset = req.imageMipTailOffset
                    for layer in 0 .. slices - 1 do
                        let mem = memory.Alloc(int64 requirements.alignment, int64 req.imageMipTailSize)
                        alwaysResident.Add mem
                       
                        let bind = 
                            VkSparseMemoryBind(
                                targetOffset,
                                req.imageMipTailSize,
                                mem.Memory.Handle,
                                uint64 mem.Offset,
                                VkSparseMemoryBindFlags.MetadataBit
                            )
                        binds.Add bind

                        targetOffset <- targetOffset + req.imageMipTailStride

            else
                if req.formatProperties.flags = VkSparseImageFormatFlags.SingleMiptailBit then
                    let mem = memory.Alloc(int64 requirements.alignment, int64 req.imageMipTailSize)
                    alwaysResident.Add mem
                    
                    let bind = 
                        VkSparseMemoryBind(
                            req.imageMipTailOffset,
                            req.imageMipTailSize,
                            mem.Memory.Handle,
                            uint64 mem.Offset,
                            VkSparseMemoryBindFlags.None
                        )

                    binds.Add bind
                else
                    let mutable targetOffset = req.imageMipTailOffset
                    for layer in 0 .. slices - 1 do
                        let mem = memory.Alloc(int64 requirements.alignment, int64 req.imageMipTailSize)
                        alwaysResident.Add mem
                       
                        let bind = 
                            VkSparseMemoryBind(
                                targetOffset,
                                req.imageMipTailSize,
                                mem.Memory.Handle,
                                uint64 mem.Offset,
                                VkSparseMemoryBindFlags.None
                            )
                        binds.Add bind

                        targetOffset <- targetOffset + req.imageMipTailStride
        
        
        let binds = CSharpList.toArray binds
        binds |> NativePtr.withA (fun pBinds ->
            let mutable images =
                VkSparseImageOpaqueMemoryBindInfo(
                    handle, uint32 binds.Length, pBinds
                )

            let mutable info =
                VkBindSparseInfo(
                    VkStructureType.BindSparseInfo, 0n,
                    0u, NativePtr.zero,
                    0u, NativePtr.zero,
                    1u, &&images,
                    0u, NativePtr.zero,
                    0u, NativePtr.zero
                
                )

            let q = device.GraphicsFamily.GetQueue()
            let f = device.CreateFence()
            lock q (fun () ->
                VkRaw.vkQueueBindSparse(q.Handle, 1u, &&info, f.Handle)
                    |> check "could not bind sparse memory"
            )
            f.Wait()
        )

        
        ()

    do printfn "%A" pageSize
    do printfn "%A (%A)" pageSizeInBytes (pageSize.X * pageSize.Y * pageSize.Z)

    let pageCounts =
        Array.init levels (fun level ->
            let s = size / (1 <<< level)
            V3i(div s.X pageSize.X, div s.Y pageSize.Y, div s.Z pageSize.Z)
        )

    let checkBind (b : SparseImageBind) =
        if b.level < 0 || b.level >= sparseLevels then
            failwith "[SparseImage] level out of bounds"

        if b.slice < 0 || b.slice >= slices then
            failwith "[SparseImage] slice out of bounds"

        if b.size.AnySmaller 0 then
            failwith "[SparseImage] size must be positive"
            
        if b.offset.AnySmaller 0 then
            failwith "[SparseImage] offset must be positive"

        let levelSize = size / (1 <<< b.level)
        let levelPageCount = div3 levelSize pageSize
        let alignedLevelSize = pageSize * levelPageCount

        let min = b.offset
        let max = b.offset + b.size - V3i.III

        if mod3 b.offset pageSize <> V3i.Zero then
            failwith "[SparseImage] non-aligned offset"

        if mod3 b.size pageSize <> V3i.Zero then
            failwith "[SparseImage] non-aligned size"
            

        if min.AnyGreaterOrEqual alignedLevelSize then
            failwith "[SparseImage] region out of bounds"

        if max.AnyGreaterOrEqual alignedLevelSize then
            failwith "[SparseImage] region out of bounds"


        match b.pointer with
            | Some ptr ->
                let pages = b.size / pageSize
                let totalSize = pageSizeInBytes * int64 pages.X * int64 pages.Y * int64 pages.Z
                if ptr.Size <> totalSize then
                    failwith "[SparseImage] non-matching memory size"
                    
            | None ->
                ()

    member x.Memory = memory

    member x.Alloc(pages : V3i) =
        let s = int64 pages.X * int64 pages.Y * int64 pages.Z * pageSizeInBytes
        memory.Alloc(pageSizeInBytes, s)

    member x.PageCount(level : int) =
        if level < 0 then failwith "level out of bounds"
        elif level >= levels then V3i.Zero
        else pageCounts.[level]

    member x.PageSize = pageSize
    

    member x.Update(bindings : list<SparseImageBind>) =
        let bindings = List.toArray bindings
        if bindings.Length > 0 then
            let binds =
                bindings |> Array.map (fun b ->
                    checkBind b

                    let o = b.offset
                    let s = b.size

                    let memory, offset =
                        match b.pointer with
                            | Some ptr -> ptr.Memory.Handle, ptr.Offset
                            | None -> VkDeviceMemory.Null, 0L

                    VkSparseImageMemoryBind(
                        VkImageSubresource(VkImageAspectFlags.ColorBit, uint32 b.level, uint32 b.slice),
                        VkOffset3D(o.X, o.Y, o.Z),
                        VkExtent3D(s.X, s.Y, s.Z),
                        memory,
                        uint64 offset,
                        VkSparseMemoryBindFlags.None
                    )

                )

            binds |> NativePtr.withA (fun pBinds ->
                let mutable images =
                    VkSparseImageMemoryBindInfo(
                        handle, uint32 binds.Length, pBinds
                    )

                let mutable info =
                    VkBindSparseInfo(
                        VkStructureType.BindSparseInfo, 0n,
                        0u, NativePtr.zero,
                        0u, NativePtr.zero,
                        0u, NativePtr.zero,
                        1u, &&images,
                        0u, NativePtr.zero
                
                    )

                let q = device.GraphicsFamily.GetQueue()
                let f = device.CreateFence()
                lock q (fun () ->
                    VkRaw.vkQueueBindSparse(q.Handle, 1u, &&info, f.Handle)
                        |> check "could not bind sparse memory"
                )

                f.Wait()
            )

    member x.Bind(level : int, slice : int, offset : V3i, size : V3i, ptr : DevicePtr) =
        x.Update [{ level = level; slice = slice; offset = offset; size = size; pointer = Some ptr }]
     
    member x.Unbind(level : int, slice : int, offset : V3i, size : V3i) =
        x.Update [{ level = level; slice = slice; offset = offset; size = size; pointer = None }]


[<AbstractClass; Sealed; Extension>]
type SparseImageDeviceExtensions private() =

    [<Extension>]
    static member CreateSparseImage(device : Device, size : V3i, levels : int, dim : TextureDimension, format : VkFormat, usage : VkImageUsageFlags) : SparseImage =

        let imageType = VkImageType.ofTextureDimension dim

        let mutable info =
            VkImageCreateInfo(
                VkStructureType.ImageCreateInfo, 0n,
                VkImageCreateFlags.SparseBindingBit ||| VkImageCreateFlags.SparseResidencyBit,
                imageType,
                format,
                VkExtent3D(size.X, size.Y, size.Z),
                uint32 levels,
                1u,
                VkSampleCountFlags.D1Bit,
                VkImageTiling.Optimal,
                usage,
                device.AllSharingMode, device.AllQueueFamiliesCnt, device.AllQueueFamiliesPtr,
                VkImageLayout.Undefined
            )

        let mutable handle = VkImage.Null
        VkRaw.vkCreateImage(device.Handle, &&info, NativePtr.zero, &&handle)
            |> check "could not create sparse image"




        SparseImage(device, handle, size, levels, 1, dim, format)

