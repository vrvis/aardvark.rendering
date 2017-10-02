﻿namespace Aardvark.Rendering.Vulkan

open System
open System.Threading
open System.Runtime.CompilerServices
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.Incremental
open Microsoft.FSharp.NativeInterop

#nowarn "9"
#nowarn "51"

type ResourceInfo<'a> =
    {
        handle      : 'a
        version     : int
    }

type ImmutableResourceDescription<'a, 'b> =
    {
        icreate          : 'a -> 'b
        idestroy         : 'b -> unit
        ieagerDestroy    : bool
    }

type MutableResourceDescription<'a, 'b> =
    {
        mcreate          : 'a -> 'b
        mdestroy         : 'b -> unit
        mtryUpdate       : 'b -> 'a -> bool
    }

type IResourceLocation =
    inherit IAdaptiveObject
    abstract member Update : AdaptiveToken -> ResourceInfo<obj>
    abstract member Acquire : unit -> unit
    abstract member Release : unit -> unit
    abstract member RelaseAll : unit -> unit
    abstract member Key : list<obj>
    abstract member Owner : IResourceCache
    

and IResourceLocation<'a> =
    inherit IResourceLocation
    abstract member Update : AdaptiveToken -> ResourceInfo<'a>

and IResourceUser =
    abstract member AddLocked       : ILockedResource -> unit
    abstract member RemoveLocked    : ILockedResource -> unit
    
and IResourceCache =
    inherit IResourceUser
    abstract member Remove          : key : list<obj> -> unit

type INativeResourceLocation<'a when 'a : unmanaged> = IResourceLocation<nativeptr<'a>>



[<AbstractClass>]
type AbstractResourceLocation<'a>(owner : IResourceCache, key : list<obj>) =
    inherit AdaptiveObject()
    
    let mutable refCount = 0

    abstract member Create : unit -> unit
    abstract member Destroy : unit -> unit
    abstract member GetHandle : AdaptiveToken -> ResourceInfo<'a>

    member x.RefCount = refCount

    member x.Acquire() =
        lock x (fun () ->
            inc &refCount
            if refCount = 1 then
                x.Create()
        )

    member x.Release() =
        lock x (fun () ->
            dec &refCount
            if refCount = 0 then
                owner.Remove key
                x.Destroy()
                let mutable foo = 0
                x.Outputs.Consume(&foo) |> ignore
                x.OutOfDate <- true
        )
  
    member x.RelaseAll() =
        lock x (fun () ->
            refCount <- 0
            owner.Remove key
            x.Destroy()
            let mutable foo = 0
            x.Outputs.Consume(&foo) |> ignore
            x.OutOfDate <- true
        )

    member x.Update(token : AdaptiveToken) =
        x.EvaluateAlways token (fun token ->
            if refCount <= 0 then failwithf "[Resource] no ref count"
            x.GetHandle token
        )
        
    interface IResourceLocation with
        member x.Update t = 
            let res = x.Update t
            { handle = res :> obj; version = res.version }

        member x.Acquire() = x.Acquire()
        member x.Release() = x.Release()
        member x.RelaseAll() = x.RelaseAll()
        member x.Owner = owner
        member x.Key = key

    interface IResourceLocation<'a> with
        member x.Update t = x.Update t
        
[<AbstractClass; Sealed; Extension>]
type ModResourceExtensionStuff() =
    [<Extension>]
    static member inline Acquire(m : IMod<'a>) =
        match m with
            | :? IOutputMod<'a> as o -> o.Acquire()
            | _ -> ()

    [<Extension>]
    static member inline Release(m : IMod<'a>) =
        match m with
            | :? IOutputMod<'a> as o -> o.Release()
            | _ -> ()
            
    [<Extension>]
    static member inline GetLocked(a : 'a) =
        match a :> obj with
            | :? ILockedResource as l -> Some l
            | _ -> None

    [<Extension>]
    static member ReplaceLocked(owner : IResourceUser, o : Option<'a>, n : Option<'a>) =
        match n with
            | Some n -> 
                match n :> obj with
                    | :? ILockedResource as n -> owner.AddLocked n
                    | _ -> ()
            | None ->
                ()

        match o with
            | Some o -> 
                match o :> obj with
                    | :? ILockedResource as o -> owner.RemoveLocked o
                    | _ -> ()
            | None ->
                ()

type ImmutableResourceLocation<'a, 'h>(owner : IResourceCache, key : list<obj>, input : IMod<'a>, desc : ImmutableResourceDescription<'a, 'h>) =
    inherit AbstractResourceLocation<'h>(owner, key)
    
    let mutable handle : Option<'a * 'h> = None

    let recreate (token : AdaptiveToken) =
        let n = input.GetValue token

        match handle with
            | Some(o,h) when Unchecked.equals o n ->
                h
            | Some(o,h) ->
                owner.ReplaceLocked (Some o, Some n)

                desc.idestroy h
                let r = desc.icreate n
                handle <- Some(n,r)
                r
            | None ->
                owner.ReplaceLocked (None, Some n)

                let r = desc.icreate n
                handle <- Some(n,r)
                r
                

    override x.Mark() =
        if desc.ieagerDestroy then 
            match handle with
                | Some(_,h) -> 
                    desc.idestroy h
                    handle <- None
                | None ->
                    ()
        true

    override x.Create() =
        input.Acquire()

    override x.Destroy() =
        input.RemoveOutput x
        match handle with
            | Some(a,h) -> 
                desc.idestroy h
                handle <- None
                owner.ReplaceLocked(Some a, None)
            | None ->
                ()
        input.Release()

    override x.GetHandle(token : AdaptiveToken) =
        if x.OutOfDate then
            let handle = recreate token
            { handle = handle; version = 0 }
        else
            match handle with
                | Some(_,h) -> { handle = h; version = 0 }
                | None -> failwith "[Resource] inconsistent state"

type MutableResourceLocation<'a, 'h>(owner : IResourceCache, key : list<obj>, input : IMod<'a>, desc : MutableResourceDescription<'a, 'h>) =
    inherit AbstractResourceLocation<'h>(owner, key)

    let mutable refCount = 0
    let mutable handle : Option<'a * 'h> = None
    let mutable version = 0


    let recreate (n : 'a) =
        match handle with
            | Some(o,h) ->
                owner.ReplaceLocked (Some o, Some n)

                desc.mdestroy h
                let r = desc.mcreate n
                handle <- Some(n,r)
                r
            | None ->
                owner.ReplaceLocked (None, Some n)

                let r = desc.mcreate n
                handle <- Some(n,r)
                r
                


    let update (token : AdaptiveToken) =
        let n = input.GetValue token

        match handle with
            | None ->
                recreate n

            | Some (oa, oh) when Unchecked.equals oa n ->
                oh

            | Some(oa,oh) ->
                if desc.mtryUpdate oh n then
                    owner.ReplaceLocked(Some oa, Some n)
                    handle <- Some(n, oh)
                    inc &version
                    oh
                else
                    recreate n


    override x.Create() =
        input.Acquire()

    override x.Destroy() =
        input.RemoveOutput x
        match handle with
            | Some(a,h) -> 
                desc.mdestroy h
                handle <- None
                owner.ReplaceLocked(Some a, None)
            | None ->
                ()
        input.Release()

    override x.GetHandle(token : AdaptiveToken) =
        if x.OutOfDate then
            let handle = update token
            { handle = handle; version = version }
        else
            match handle with
                | Some(_,h) -> { handle = h; version = version }
                | None -> failwith "[Resource] inconsistent state"

[<AbstractClass>]
type AbstractPointerResource<'a when 'a : unmanaged>(owner : IResourceCache, key : list<obj>) =
    inherit AbstractResourceLocation<nativeptr<'a>>(owner, key)

    let mutable ptr = NativePtr.zero
    let mutable version = 0

    abstract member Compute : AdaptiveToken -> 'a
    abstract member Free : 'a -> unit
    default x.Free _ = ()

    override x.Create() =
        ()
    override x.Destroy() =
        if not (NativePtr.isNull ptr) then
            let v = NativePtr.read ptr
            x.Free v
            NativePtr.free ptr

    override x.GetHandle(token : AdaptiveToken) =
        if x.OutOfDate then
            let value = x.Compute token

            if not (NativePtr.isNull ptr) then
                let v = NativePtr.read ptr
                x.Free v
            else
                ptr <- NativePtr.alloc 1

            NativePtr.write ptr value
            inc &version
            { handle = ptr; version = version }
        else
            { handle = ptr; version = version }


type ResourceLocationCache<'h>(user : IResourceUser) =
    let store = System.Collections.Concurrent.ConcurrentDictionary<list<obj>, IResourceLocation<'h>>()

    member x.GetOrCreate(key : list<obj>, create : IResourceCache -> list<obj> -> #IResourceLocation<'h>) =
        let res = 
            store.GetOrAdd(key, fun key -> 
                let res = create x key :> IResourceLocation<_>
                res
            )
        res

    member x.Clear() =
        let res = store.Values |> Seq.toArray
        for r in res do r.RelaseAll()

    interface IResourceCache with
        member x.AddLocked l = user.AddLocked l
        member x.RemoveLocked l = user.RemoveLocked l
        member x.Remove key = store.TryRemove key |> ignore


open Aardvark.Rendering.Vulkan


module Resources =

    type AdaptiveDescriptor =
        | AdaptiveUniformBuffer of int * IResourceLocation<UniformBuffer>
        | AdaptiveCombinedImageSampler of int * array<Option<IResourceLocation<ImageView> * IResourceLocation<Sampler>>>


    type BufferResource(owner : IResourceCache, key : list<obj>, device : Device, usage : VkBufferUsageFlags, input : IMod<IBuffer>) =
        inherit ImmutableResourceLocation<IBuffer, Buffer>(
            owner, key, 
            input,
            {
                icreate = fun (b : IBuffer) -> device.CreateBuffer(usage, b)
                idestroy = fun b -> device.Delete b
                ieagerDestroy = true
            }
        )

    type IndirectBufferResource(owner : IResourceCache, key : list<obj>, device : Device, indexed : bool, input : IMod<IIndirectBuffer>) =
        inherit ImmutableResourceLocation<IIndirectBuffer, IndirectBuffer>(
            owner, key, 
            input,
            {
                icreate = fun (b : IIndirectBuffer) -> device.CreateIndirectBuffer(indexed, b)
                idestroy = fun b -> device.Delete b
                ieagerDestroy = true
            }
        )

    type UniformBufferResource(owner : IResourceCache, key : list<obj>, device : Device, layout : UniformBufferLayout, writers : list<IMod * UniformWriters.IWriter>) =
        inherit AbstractResourceLocation<UniformBuffer>(owner, key)
        
        let mutable handle : Option<UniformBuffer> = None
        let mutable version = 0

        override x.Create() =
            ()

        override x.Destroy() =
            match handle with
                | Some b -> 
                    device.Delete b
                    handle <- None
                | _ -> ()
                
        override x.GetHandle(token : AdaptiveToken) =
            if x.OutOfDate then
                let handle =
                    match handle with
                        | Some h -> h
                        | None ->
                            let h = device.CreateUniformBuffer(layout)
                            handle <- Some h
                            h


                for (m,w) in writers do
                    w.Write(token, m, handle.Storage.Pointer)

                device.Upload handle

                inc &version
                { handle = handle; version = version }
            else
                match handle with
                    | Some h -> { handle = h; version = version }
                    | None -> failwith "[Resource] inconsistent state"

    type ImageResource(owner : IResourceCache, key : list<obj>, device : Device, input : IMod<ITexture>) =
        inherit ImmutableResourceLocation<ITexture, Image>(
            owner, key, 
            input,
            {
                icreate = fun (b : ITexture) -> device.CreateImage(b)
                idestroy = fun b -> device.Delete b
                ieagerDestroy = true
            }
        )

    type SamplerResource(owner : IResourceCache, key : list<obj>, device : Device, input : IMod<SamplerStateDescription>) =
        inherit ImmutableResourceLocation<SamplerStateDescription, Sampler>(
            owner, key, 
            input,
            {
                icreate = fun (b : SamplerStateDescription) -> device.CreateSampler(b)
                idestroy = fun b -> device.Delete b
                ieagerDestroy = true
            }
        )
        

    type ShaderProgramEffectResource(owner : IResourceCache, key : list<obj>, device : Device, layout : PipelineLayout, input : IMod<FShade.Imperative.Module>) =
        inherit ImmutableResourceLocation<FShade.Imperative.Module, ShaderProgram>(
            owner, key, 
            input,
            {
                icreate = fun (e : FShade.Imperative.Module) -> ShaderProgram.ofModule layout e device
                idestroy = fun b -> device.Delete b
                ieagerDestroy = false
            }
        )

        
    type ShaderProgramResource(owner : IResourceCache, key : list<obj>, device : Device, signature : IFramebufferSignature, input : ISurface) =
        inherit ImmutableResourceLocation<ISurface, ShaderProgram>(
            owner, key, 
            Mod.constant input,
            {
                icreate = fun (b : ISurface) -> device.CreateShaderProgram(signature, b)
                idestroy = fun b -> device.Delete b
                ieagerDestroy = false
            }
        )

    type InputAssemblyStateResource(owner : IResourceCache, key : list<obj>, input : IMod<IndexedGeometryMode>) =
        inherit AbstractPointerResource<VkPipelineInputAssemblyStateCreateInfo>(owner, key)

        override x.Compute(token) =
            let m = input.GetValue token
            let res = InputAssemblyState.ofIndexedGeometryMode m
            VkPipelineInputAssemblyStateCreateInfo(
                VkStructureType.PipelineInputAssemblyStateCreateInfo, 0n,
                VkPipelineInputAssemblyStateCreateFlags.MinValue,
                res.topology,
                (if res.restartEnable then 1u else 0u)
            )

    type VertexInputStateResource(owner : IResourceCache, key : list<obj>, prog : PipelineInfo, input : IMod<Map<Symbol, VertexInputDescription>>) =
        inherit AbstractPointerResource<VkPipelineVertexInputStateCreateInfo>(owner, key)
        static let collecti (f : int -> 'a -> list<'b>) (m : list<'a>) =
            m |> List.indexed |> List.collect (fun (i,v) -> f i v)

        override x.Free(state : VkPipelineVertexInputStateCreateInfo) =
            NativePtr.free state.pVertexAttributeDescriptions
            NativePtr.free state.pVertexBindingDescriptions

        override x.Compute(token) =
            let state = input.GetValue token

            let inputs = prog.pInputs |> List.sortBy (fun p -> p.location)

            let paramsWithInputs =
                inputs |> List.map (fun p ->
                    match Map.tryFind p.semantic state with
                        | Some ip -> 
                            p.location, p, ip
                        | None ->
                            failf "could not get vertex input-type for %A" p
                )

            let inputBindings =
                paramsWithInputs |> List.mapi (fun i (loc, p, ip) ->
                    VkVertexInputBindingDescription(
                        uint32 i,
                        uint32 ip.stride,
                        ip.stepRate
                    )
                ) |> List.toArray

            let inputAttributes =
                paramsWithInputs |> collecti (fun bi (loc, p, ip) ->
                    ip.offsets |> List.mapi (fun i off ->
                        VkVertexInputAttributeDescription(
                            uint32 (loc + i),
                            uint32 bi,
                            ip.inputFormat,
                            uint32 off
                        )
                    )
                ) |> List.toArray

            let pInputBindings = NativePtr.alloc inputBindings.Length
            let pInputAttributes = NativePtr.alloc inputAttributes.Length

            for i in 0 .. inputBindings.Length - 1 do
                NativePtr.set pInputBindings i inputBindings.[i]

            for i in 0 .. inputAttributes.Length - 1 do
                NativePtr.set pInputAttributes i inputAttributes.[i]

            VkPipelineVertexInputStateCreateInfo(
                VkStructureType.PipelineVertexInputStateCreateInfo, 0n, 
                VkPipelineVertexInputStateCreateFlags.MinValue,

                uint32 inputBindings.Length,
                pInputBindings,

                uint32 inputAttributes.Length,
                pInputAttributes
            )


    type DepthStencilStateResource(owner : IResourceCache, key : list<obj>, depthWrite : bool, depth : IMod<DepthTestMode>, stencil : IMod<StencilMode>) =
        inherit AbstractPointerResource<VkPipelineDepthStencilStateCreateInfo>(owner, key)

        override x.Compute(token) =
            let depth = depth.GetValue token
            let stencil = stencil.GetValue token

            let depth = DepthState.create depthWrite depth
            let stencil = StencilState.create stencil

            VkPipelineDepthStencilStateCreateInfo(
                VkStructureType.PipelineDepthStencilStateCreateInfo, 0n,
                VkPipelineDepthStencilStateCreateFlags.MinValue,
                (if depth.testEnabled then 1u else 0u),
                (if depth.writeEnabled then 1u else 0u),
                depth.compare,
                (if depth.boundsTest then 1u else 0u),
                (if stencil.enabled then 1u else 0u),
                stencil.front,
                stencil.back,
                float32 depth.depthBounds.Min,
                float32 depth.depthBounds.Max
            )
            
    type RasterizerStateResource(owner : IResourceCache, key : list<obj>, depth : IMod<DepthTestMode>, cull : IMod<CullMode>, fill : IMod<FillMode>) =
        inherit AbstractPointerResource<VkPipelineRasterizationStateCreateInfo>(owner, key)

        override x.Compute(token) =
            let depth = depth.GetValue token
            let cull = cull.GetValue token
            let fill = fill.GetValue token
            let state = RasterizerState.create false depth cull fill

            VkPipelineRasterizationStateCreateInfo(
                VkStructureType.PipelineRasterizationStateCreateInfo, 0n,
                VkPipelineRasterizationStateCreateFlags.MinValue,
                (if state.depthClampEnable then 1u else 0u),
                0u,
                state.polygonMode,
                state.cullMode,
                state.frontFace,
                (if state.depthBiasEnable then 1u else 0u),
                float32 state.depthBiasConstantFactor,
                float32 state.depthBiasClamp,
                float32 state.depthBiasSlopeFactor,
                float32 state.lineWidth
            )
    
    type ColorBlendStateResource(owner : IResourceCache, key : list<obj>, writeMasks : bool[], blend : IMod<BlendMode>) =
        inherit AbstractPointerResource<VkPipelineColorBlendStateCreateInfo>(owner, key)

        override x.Free(h : VkPipelineColorBlendStateCreateInfo) =
            NativePtr.free h.pAttachments
            
        override x.Compute(token) =
            let blend = blend.GetValue token
            let state = ColorBlendState.create writeMasks writeMasks.Length blend
            let pAttStates = NativePtr.alloc writeMasks.Length

            for i in 0 .. state.attachmentStates.Length - 1 do
                let s = state.attachmentStates.[i]
                let att = 
                    VkPipelineColorBlendAttachmentState(
                        (if s.enabled then 1u else 0u),
                        s.srcFactor,
                        s.dstFactor,
                        s.operation,
                        s.srcFactorAlpha,
                        s.dstFactorAlpha,
                        s.operationAlpha,
                        s.colorWriteMask
                    )
                NativePtr.set pAttStates i att


            VkPipelineColorBlendStateCreateInfo(
                VkStructureType.PipelineColorBlendStateCreateInfo, 0n,
                VkPipelineColorBlendStateCreateFlags.MinValue,
                (if state.logicOpEnable then 1u else 0u),
                state.logicOp,
                uint32 writeMasks.Length,
                pAttStates,
                state.constants
            )
    
    type DirectDrawCallResource(owner : IResourceCache, key : list<obj>, indexed : bool, calls : IMod<list<DrawCallInfo>>) =
        inherit AbstractPointerResource<DrawCall>(owner, key)
        
        override x.Free(call : DrawCall) =
            call.Dispose()

        override x.Compute(token) =
            let calls = calls.GetValue token
            DrawCall.Direct(indexed, List.toArray calls)


    type DescriptorSetResource(owner : IResourceCache, key : list<obj>, pool : DescriptorPool, layout : DescriptorSetLayout, bindings : list<AdaptiveDescriptor>) =
        inherit AbstractResourceLocation<DescriptorSet>(owner, key)

        let mutable handle = None
        let mutable version = 0

        override x.Create() =
            for b in bindings do
                match b with
                    | AdaptiveCombinedImageSampler(_,arr) ->
                        for a in arr do
                            match a with
                                | Some (i,s) -> i.Acquire(); s.Acquire()
                                | None -> ()
                    | AdaptiveUniformBuffer(_,b) ->
                        b.Acquire()

            ()

        override x.Destroy() =
            for b in bindings do
                match b with
                    | AdaptiveCombinedImageSampler(_,arr) ->
                        for a in arr do
                            match a with
                                | Some (i,s) -> i.Release(); s.Release()
                                | None -> ()
                    | AdaptiveUniformBuffer(_,b) ->
                        b.Release()

            match handle with
                | Some set -> 
                    pool.Free set
                    handle <- None
                | _ -> ()

        override x.GetHandle(token : AdaptiveToken) =
            if x.OutOfDate then
                
                let bindings =
                    bindings |> List.toArray |> Array.map (fun b ->
                        match b with
                            | AdaptiveUniformBuffer(slot, b) ->
                                UniformBuffer(slot, b.Update(AdaptiveToken.Top).handle)

                            | AdaptiveCombinedImageSampler(slot, arr) ->
                                let arr =
                                    arr |> Array.map (fun o ->
                                        match o with
                                            | Some(s,i) ->
                                                Some(s.Update(token).handle, i.Update(token).handle)
                                            | None ->
                                                None
                                    )

                                CombinedImageSampler(slot, arr)
                    )

                let handle =
                    match handle with
                        | Some h -> h
                        | None ->
                            let h = pool.Alloc(layout)
                            handle <- Some h
                            h

                pool.Update(handle, bindings)
                inc &version

                { handle = handle; version = version }
            else 
                match handle with
                    | Some h -> { handle = h; version = version }
                    | None -> failwith "[Resource] inconsistent state"

    type PipelineResource(owner : IResourceCache, key : list<obj>, 
                          renderPass : RenderPass,
                          program : IResourceLocation<ShaderProgram>, 
                          inputState : INativeResourceLocation<VkPipelineVertexInputStateCreateInfo>,
                          inputAssembly : INativeResourceLocation<VkPipelineInputAssemblyStateCreateInfo>,
                          rasterizerState : INativeResourceLocation<VkPipelineRasterizationStateCreateInfo>,
                          colorBlendState : INativeResourceLocation<VkPipelineColorBlendStateCreateInfo>,
                          multisample : MultisampleState,
                          depthStencil : INativeResourceLocation<VkPipelineDepthStencilStateCreateInfo>
                         ) =
        inherit AbstractResourceLocation<nativeptr<VkPipeline>>(owner, key)

        let mutable handle : Option<Pipeline> = None
        let mutable pointer : nativeptr<VkPipeline> = NativePtr.zero
        let mutable version = 0

        static let check str err =
            if err <> VkResult.VkSuccess then failwithf "[Vulkan] %s" str

        override x.Create() =
            program.Acquire()
            inputState.Acquire()
            inputAssembly.Acquire()
            rasterizerState.Acquire()
            colorBlendState.Acquire()
            depthStencil.Acquire()
            pointer <- NativePtr.alloc 1

        override x.Destroy() =
            program.Release()
            inputState.Release()
            inputAssembly.Release()
            rasterizerState.Release()
            colorBlendState.Release()
            depthStencil.Release()
            NativePtr.free pointer
            

        override x.GetHandle(token : AdaptiveToken) =
            if x.OutOfDate then
                let program = program.Update token
                
                let prog = program.handle
                let device = prog.Device

                let pipeline = 
                    prog.ShaderCreateInfos |> NativePtr.withA (fun pShaderCreateInfos ->

                        let mutable viewportState =
                            let vp = renderPass.AttachmentCount
                            VkPipelineViewportStateCreateInfo(
                                VkStructureType.PipelineViewportStateCreateInfo, 0n,
                                VkPipelineViewportStateCreateFlags.MinValue,
                
                                uint32 vp,
                                NativePtr.zero,

                                uint32 vp,
                                NativePtr.zero
                            )

                        let pSampleMasks = NativePtr.pushStackArray multisample.sampleMask
                        let mutable multisampleState =
                            let ms = multisample
                            VkPipelineMultisampleStateCreateInfo(
                                VkStructureType.PipelineMultisampleStateCreateInfo, 0n,
                                VkPipelineMultisampleStateCreateFlags.MinValue,
                
                                unbox ms.samples,
                                (if ms.sampleShadingEnable then 1u else 0u),
                                float32 ms.minSampleShading,
                                pSampleMasks,
                                (if ms.alphaToCoverageEnable then 1u else 0u),
                                (if ms.alphaToOneEnable then 1u else 0u)
                            )
            
                        let dynamicStates = [| VkDynamicState.Viewport; VkDynamicState.Scissor |]
        
                        let pDynamicStates = NativePtr.pushStackArray dynamicStates
            
                        let mutable dynamicStates =
                            VkPipelineDynamicStateCreateInfo(
                                VkStructureType.PipelineDynamicStateCreateInfo, 0n,
                                VkPipelineDynamicStateCreateFlags.MinValue, 

                                uint32 dynamicStates.Length,
                                pDynamicStates
                            )

                        // TODO: tessellation input-patch-size

                        let inputState = inputState.Update(token).handle
                        let inputAssembly = inputAssembly.Update(token).handle
                        let rasterizerState = rasterizerState.Update(token).handle
                        let depthStencil = depthStencil.Update(token).handle
                        let colorBlendState = colorBlendState.Update(token).handle

                        let mutable desc =
                            VkGraphicsPipelineCreateInfo(
                                VkStructureType.GraphicsPipelineCreateInfo, 0n,
                                VkPipelineCreateFlags.None,
                                uint32 prog.ShaderCreateInfos.Length,
                                pShaderCreateInfos,
                                inputState,
                                inputAssembly,
                                NativePtr.zero, // tessellation
                                &&viewportState,
                                rasterizerState,
                                &&multisampleState,
                                depthStencil,
                                colorBlendState,
                                &&dynamicStates, //dynamic
                                prog.PipelineLayout.Handle,
                                renderPass.Handle,
                                0u,
                                VkPipeline.Null,
                                0
                            )

                        let mutable handle = VkPipeline.Null
                        VkRaw.vkCreateGraphicsPipelines(device.Handle, VkPipelineCache.Null, 1u, &&desc, NativePtr.zero, &&handle)
                            |> check "could not create pipeline"

                        Pipeline(device, handle, Unchecked.defaultof<_>)

                    )

                match handle with
                    | Some h -> device.Delete h
                    | None -> ()

                handle <- Some pipeline
                NativePtr.write pointer pipeline.Handle

                inc &version
                
                { handle = pointer; version = version }
            else
                { handle = pointer; version = version }
    
    type IndirectDrawCallResource(owner : IResourceCache, key : list<obj>, indexed : bool, calls : IResourceLocation<IndirectBuffer>) =
        inherit AbstractResourceLocation<nativeptr<DrawCall>>(owner, key)

        let mutable handle : Option<DrawCall> = None
        let mutable pointer = NativePtr.zero
        let mutable version = 0

        override x.Create() =
            calls.Acquire()
            pointer <- NativePtr.alloc 1

        override x.Destroy() =
            match handle with   
                | Some h -> 
                    h.Dispose()
                    handle <- None
                | None -> ()

            NativePtr.free pointer
            calls.Release()

        override x.GetHandle(token : AdaptiveToken) =
            if x.OutOfDate then
                let calls = calls.Update token

                match handle with
                    | Some h -> h.Dispose()
                    | _ -> ()

                let call = DrawCall.Indirect(indexed, calls.handle.Handle, calls.handle.Count)
                handle <- Some call
                NativePtr.write pointer call

                inc &version

                { handle = pointer; version = version }
            else
                { handle = pointer; version = version }

    type BufferBindingResource(owner : IResourceCache, key : list<obj>, buffers : list<IResourceLocation<Buffer> * int64>) =
        inherit AbstractResourceLocation<nativeptr<VertexBufferBinding>>(owner, key)

        let mutable handle : Option<VertexBufferBinding> = None
        let mutable pointer = NativePtr.zero
        let mutable version = 0

        override x.Create() =
            for (b,_) in buffers do b.Acquire()
            pointer <- NativePtr.alloc 1

        override x.Destroy() =
            match handle with   
                | Some h -> 
                    h.Dispose()
                    handle <- None
                | None -> ()

            NativePtr.free pointer
            for (b,_) in buffers do b.Release()

        override x.GetHandle(token : AdaptiveToken) =
            if x.OutOfDate then
                let calls = buffers |> List.map (fun (b,o) -> b.Update(token).handle.Handle, o) //calls.Update token

                match handle with
                    | Some h -> h.Dispose()
                    | _ -> ()

                let call = new VertexBufferBinding(0, List.toArray calls)
                handle <- Some call
                NativePtr.write pointer call

                inc &version

                { handle = pointer; version = version }
            else
                { handle = pointer; version = version }

    type DescriptorSetBindingResource(owner : IResourceCache, key : list<obj>, layout : PipelineLayout, sets : list<IResourceLocation<DescriptorSet>>) =
        inherit AbstractResourceLocation<nativeptr<DescriptorSetBinding>>(owner, key)

        let sets = List.toArray sets

        let mutable handle : nativeptr<DescriptorSetBinding> = NativePtr.zero
        let mutable setVersions = Array.init sets.Length (fun _ -> -1)
        let mutable version = 0

        override x.Create() =
            for s in sets do s.Acquire()
            let ptr = NativePtr.alloc 1
            let value = new DescriptorSetBinding(layout.Handle, 0, sets.Length)
            NativePtr.write ptr value
            handle <- ptr

        override x.Destroy() =
            for s in sets do s.Release()
            NativePtr.free handle
            handle <- NativePtr.zero
            setVersions <- Array.init sets.Length (fun _ -> -1)

        override x.GetHandle(token : AdaptiveToken) =
            if x.OutOfDate then
                let mutable changed = false
                let target = NativePtr.read handle
                
                for i in 0 .. sets.Length - 1 do
                    let info = sets.[i].Update(token)
                    NativePtr.set target.Sets i info.handle.Handle
                    if info.version <> setVersions.[i] then
                        setVersions.[i] <- info.version
                        changed <- true

                if changed then inc &version

                { handle = handle; version = version }
            else
                { handle = handle; version = version }
 
    type IndexBufferBindingResource(owner : IResourceCache, key : list<obj>, indexType : VkIndexType, index : IResourceLocation<Buffer>) =
        inherit AbstractResourceLocation<nativeptr<IndexBufferBinding>>(owner, key)

        let mutable handle : Option<IndexBufferBinding> = None
        let mutable pointer = NativePtr.zero
        let mutable version = 0

        override x.Create() =
            index.Acquire()
            pointer <- NativePtr.alloc 1

        override x.Destroy() =
            match handle with   
                | Some h -> 
                    handle <- None
                | None -> ()

            NativePtr.free pointer
            index.Release()

        override x.GetHandle(token : AdaptiveToken) =
            if x.OutOfDate then
                let index = index.Update token

                let ibo = IndexBufferBinding(index.handle.Handle, indexType)
                handle <- Some ibo
                NativePtr.write pointer ibo

                inc &version

                { handle = pointer; version = version }
            else
                { handle = pointer; version = version }
 
    type ImageViewResource(owner : IResourceCache, key : list<obj>, device : Device, image : IResourceLocation<Image>) =
        inherit AbstractResourceLocation<ImageView>(owner, key)

        let mutable handle : Option<ImageView> = None

        override x.Create() =
            image.Acquire()

        override x.Destroy() =
            match handle with   
                | Some h -> 
                    device.Delete h
                    handle <- None
                | None -> ()
            image.Release()

        override x.GetHandle(token : AdaptiveToken) =
            if x.OutOfDate then
                let image = image.Update token

                match handle with
                    | Some h -> device.Delete h
                    | None -> ()

                let h = device.CreateImageView(image.handle, VkComponentMapping.Identity)
                handle <- Some h

                { handle = h; version = 0 }
            else
                match handle with
                    | Some h -> { handle = h; version = 0 }
                    | None -> failwith "[Resource] inconsistent state"
    
    type IsActiveResource(owner : IResourceCache, key : list<obj>, input : IMod<bool>) =
        inherit AbstractPointerResource<int>(owner, key)

        override x.Compute (token : AdaptiveToken) =
            if input.GetValue token then 1 else 0

open Resources
type ResourceManager(user : IResourceUser, device : Device) =
    let descriptorPool = device.CreateDescriptorPool(1 <<< 22, 1 <<< 22)

    let bufferCache             = ResourceLocationCache<Buffer>(user)
    let indirectBufferCache     = ResourceLocationCache<IndirectBuffer>(user)
    let indexBufferCache        = ResourceLocationCache<Buffer>(user)
    let descriptorSetCache      = ResourceLocationCache<DescriptorSet>(user)
    let uniformBufferCache      = ResourceLocationCache<UniformBuffer>(user)
    let imageCache              = ResourceLocationCache<Image>(user)
    let imageViewCache          = ResourceLocationCache<ImageView>(user)
    let samplerCache            = ResourceLocationCache<Sampler>(user)
    let programCache            = ResourceLocationCache<ShaderProgram>(user)
    let simpleSurfaceCache      = System.Collections.Concurrent.ConcurrentDictionary<obj, ShaderProgram>()
    let fshadeThingCache        = System.Collections.Concurrent.ConcurrentDictionary<obj, PipelineLayout * IMod<FShade.Imperative.Module>>()
    
    let vertexInputCache        = ResourceLocationCache<nativeptr<VkPipelineVertexInputStateCreateInfo>>(user)
    let inputAssemblyCache      = ResourceLocationCache<nativeptr<VkPipelineInputAssemblyStateCreateInfo>>(user)
    let depthStencilCache       = ResourceLocationCache<nativeptr<VkPipelineDepthStencilStateCreateInfo>>(user)
    let rasterizerStateCache    = ResourceLocationCache<nativeptr<VkPipelineRasterizationStateCreateInfo>>(user)
    let colorBlendStateCache    = ResourceLocationCache<nativeptr<VkPipelineColorBlendStateCreateInfo>>(user)
    let pipelineCache           = ResourceLocationCache<nativeptr<VkPipeline>>(user)

    let drawCallCache           = ResourceLocationCache<nativeptr<DrawCall>>(user)
    let bufferBindingCache      = ResourceLocationCache<nativeptr<VertexBufferBinding>>(user)
    let descriptorBindingCache  = ResourceLocationCache<nativeptr<DescriptorSetBinding>>(user)
    let indexBindingCache       = ResourceLocationCache<nativeptr<IndexBufferBinding>>(user)
    let isActiveCache           = ResourceLocationCache<nativeptr<int>>(user)


    member x.ResourceUser = user

    member x.Dispose() =
        device.Delete descriptorPool
        bufferCache.Clear()

        indirectBufferCache.Clear()
        indexBufferCache.Clear()
        descriptorSetCache.Clear()
        uniformBufferCache.Clear()
        imageCache.Clear()
        imageViewCache.Clear()
        samplerCache.Clear()
        programCache.Clear()

        vertexInputCache.Clear()
        inputAssemblyCache.Clear()
        depthStencilCache.Clear()
        rasterizerStateCache.Clear()
        colorBlendStateCache.Clear()
        pipelineCache.Clear()

        drawCallCache.Clear()
        bufferBindingCache.Clear()
        descriptorBindingCache.Clear()
        indexBindingCache.Clear()
        isActiveCache.Clear()



    member x.Device = device
    member x.DescriptorPool = descriptorPool

    member x.CreateRenderPass(signature : Map<Symbol, AttachmentSignature>) =
        device.CreateRenderPass(signature)

    member x.CreateBuffer(input : IMod<IBuffer>) =
        bufferCache.GetOrCreate([input :> obj], fun cache key -> new BufferResource(cache, key, device, VkBufferUsageFlags.TransferDstBit ||| VkBufferUsageFlags.VertexBufferBit, input))
        
    member x.CreateIndexBuffer(input : IMod<IBuffer>) =
        bufferCache.GetOrCreate([input :> obj], fun cache key -> new BufferResource(cache, key, device, VkBufferUsageFlags.TransferDstBit ||| VkBufferUsageFlags.IndexBufferBit, input))
        
    member x.CreateIndirectBuffer(indexed : bool, input : IMod<IIndirectBuffer>) =
        indirectBufferCache.GetOrCreate([indexed :> obj; input :> obj], fun cache key -> new IndirectBufferResource(cache, key, device, indexed, input))

    member x.CreateImage(input : IMod<ITexture>) =
        imageCache.GetOrCreate([input :> obj], fun cache key -> new ImageResource(cache, key, device, input))
        
    member x.CreateImageView(input : IResourceLocation<Image>) =
        imageViewCache.GetOrCreate([input :> obj], fun cache key -> new ImageViewResource(cache, key, device, input))
        
    member x.CreateSampler(data : IMod<SamplerStateDescription>) =
        samplerCache.GetOrCreate([data :> obj], fun cache key -> new SamplerResource(cache, key, device, data))
        
    member x.CreateShaderProgram(signature : IFramebufferSignature, data : ISurface) =
        let programKey = (signature, data) :> obj

        let program = 
            simpleSurfaceCache.GetOrAdd(programKey, fun _ ->
                device.CreateShaderProgram(signature, data)
            )

        let resource = 
            programCache.GetOrCreate([program :> obj], fun cache key -> 
                { new AbstractResourceLocation<ShaderProgram>(cache, key) with
                    override x.Create () = ()
                    override x.Destroy () = ()
                    override x.GetHandle t = { handle = program; version = 0 }
                }
            )
        program.PipelineLayout, resource

    member x.CreateShaderProgram(layout : PipelineLayout, data : IMod<FShade.Imperative.Module>) =
        programCache.GetOrCreate([layout :> obj; data :> obj], fun cache key -> 
            let prog = new ShaderProgramEffectResource(cache, key, device, layout, data)
            prog.Acquire()
            prog
        )

    member x.CreateShaderProgram(signature : IFramebufferSignature, data : Aardvark.Base.Surface) =
        match data with
            | Surface.FShadeSimple effect ->
                x.CreateShaderProgram(signature, FShadeSurface.Get(effect))
                
            | Surface.FShade(compile) -> 
                let layout, module_ = 
                    fshadeThingCache.GetOrAdd((signature, compile) :> obj, fun _ ->
                        let outputs = 
                            signature.ColorAttachments
                                |> Map.toList
                                |> List.map (fun (idx, (name, att)) -> string name, (att.GetType name, idx))
                                |> Map.ofList
            
                        let layout, module_ = 
                            compile { 
                                PipelineInfo.fshadeConfig with 
                                    outputs = outputs
                            }
                        let layout = device.CreatePipelineLayout(layout)

                        layout, module_
                    )

                layout, x.CreateShaderProgram(layout, module_)

            | Surface.Backend s -> 
                
                x.CreateShaderProgram(signature, s)

            | Surface.None -> 
                failwith "[Vulkan] encountered empty surface"
    
    member x.CreateUniformBuffer(scope : Ag.Scope, layout : UniformBufferLayout, u : IUniformProvider, additional : SymbolDict<IMod>) =
        let values =
            layout.fields 
            |> List.map (fun (f) ->
                let sem = Symbol.Create f.name
                match Uniforms.tryGetDerivedUniform f.name u with
                    | Some r -> f, r
                    | None -> 
                        match u.TryGetUniform(scope, sem) with
                            | Some v -> f, v
                            | None -> 
                                match additional.TryGetValue sem with
                                    | (true, m) -> f, m
                                    | _ -> failwithf "[Vulkan] could not get uniform: %A" f
            )

        let writers = 
            values |> List.map (fun (target, m) ->
                match m.GetType() with
                    | ModOf tSource -> m, UniformWriters.getWriter target.offset target.fieldType tSource
                    | t -> failwithf "[UniformBuffer] unexpected input-type %A" t
            )

        let key = (layout :> obj) :: (values |> List.map (fun (_,v) -> v :> obj))
        uniformBufferCache.GetOrCreate(key, fun cache key -> UniformBufferResource(cache, key, device, layout, writers))

    member x.CreateDescriptorSet(layout : DescriptorSetLayout, bindings : list<AdaptiveDescriptor>) =
        descriptorSetCache.GetOrCreate([layout :> obj; bindings :> obj], fun cache key -> new DescriptorSetResource(cache, key, descriptorPool, layout, bindings))
        
    member x.CreateVertexInputState(program : PipelineInfo, mode : IMod<Map<Symbol, VertexInputDescription>>) =
        vertexInputCache.GetOrCreate([program :> obj; mode :> obj], fun cache key -> new VertexInputStateResource(cache, key, program, mode))

    member x.CreateInputAssemblyState(mode : IMod<IndexedGeometryMode>) =
        inputAssemblyCache.GetOrCreate([mode :> obj], fun cache key -> new InputAssemblyStateResource(cache, key, mode))

    member x.CreateDepthStencilState(depthWrite : bool, depth : IMod<DepthTestMode>, stencil : IMod<StencilMode>) =
        depthStencilCache.GetOrCreate([depthWrite :> obj; depth :> obj; stencil :> obj], fun cache key -> new DepthStencilStateResource(cache, key, depthWrite, depth, stencil))
        
    member x.CreateRasterizerState(depth : IMod<DepthTestMode>, cull : IMod<CullMode>, fill : IMod<FillMode>) =
        rasterizerStateCache.GetOrCreate([depth :> obj; cull :> obj; fill :> obj], fun cache key -> new RasterizerStateResource(cache, key, depth, cull, fill))

    member x.CreateColorBlendState(pass : RenderPass, writeBuffers : Option<Set<Symbol>>, blend : IMod<BlendMode>) =
        colorBlendStateCache.GetOrCreate(
            [pass :> obj; writeBuffers :> obj; blend :> obj], 
            fun cache key -> 
                let writeBuffers =
                    match writeBuffers with
                        | Some set -> 
                            if Set.isSuperset set pass.Semantics then pass.Semantics
                            else set
                        | None ->
                            pass.Semantics

                let writeMasks = Array.zeroCreate pass.ColorAttachmentCount
                for (i, (sem,_)) in Map.toSeq pass.ColorAttachments do 
                    if Set.contains sem writeBuffers then writeMasks.[i] <- true
                    else writeMasks.[i] <- false

                new ColorBlendStateResource(cache, key, writeMasks, blend)
        )

    member x.CreatePipeline(program         : IResourceLocation<ShaderProgram>, 
                            pass            : RenderPass,
                            inputState      : INativeResourceLocation<VkPipelineVertexInputStateCreateInfo>,
                            inputAssembly   : INativeResourceLocation<VkPipelineInputAssemblyStateCreateInfo>,
                            rasterizerState : INativeResourceLocation<VkPipelineRasterizationStateCreateInfo>,
                            colorBlendState : INativeResourceLocation<VkPipelineColorBlendStateCreateInfo>,
                            depthStencil    : INativeResourceLocation<VkPipelineDepthStencilStateCreateInfo>,
                            writeBuffers    : Option<Set<Symbol>>
                        ) =

        //let programHandle = program.Update(AdaptiveToken.Top).handle

        let anyAttachment = 
            match pass.ColorAttachments |> Map.toSeq |> Seq.tryHead with
                | Some (_,(_,a)) -> a
                | None -> pass.DepthStencilAttachment |> Option.get

        //let inputs = VertexInputState.create inputs
        // TODO: sampleShading
        let ms = MultisampleState.create false anyAttachment.samples
        let key = [ program :> obj; inputState :> obj; inputAssembly :> obj; rasterizerState :> obj; colorBlendState :> obj; ms :> obj; depthStencil :> obj ]
        pipelineCache.GetOrCreate(
            key,
            fun cache key ->
                new PipelineResource(
                    cache, key,
                    pass,
                    program,
                    inputState,
                    inputAssembly,
                    rasterizerState,
                    colorBlendState,
                    ms,
                    depthStencil
                )

        )


    member x.CreateDrawCall(indexed : bool, calls : IMod<list<DrawCallInfo>>) =
        drawCallCache.GetOrCreate([indexed :> obj; calls :> obj], fun cache key -> new DirectDrawCallResource(cache, key, indexed, calls))

    member x.CreateDrawCall(indexed : bool, calls : IResourceLocation<IndirectBuffer>) =
        drawCallCache.GetOrCreate([indexed :> obj; calls :> obj], fun cache key -> new IndirectDrawCallResource(cache, key, indexed, calls))
        
    member x.CreateVertexBufferBinding(buffers : list<IResourceLocation<Buffer> * int64>) =
        bufferBindingCache.GetOrCreate([buffers :> obj], fun cache key -> new BufferBindingResource(cache, key, buffers))

    member x.CreateDescriptorSetBinding(layout : PipelineLayout, bindings : list<IResourceLocation<DescriptorSet>>) =
        descriptorBindingCache.GetOrCreate([layout :> obj; bindings :> obj], fun cache key -> new DescriptorSetBindingResource(cache, key, layout, bindings))
        
    member x.CreateIndexBufferBinding(binding : IResourceLocation<Buffer>, t : VkIndexType) =
        indexBindingCache.GetOrCreate([binding :> obj; t :> obj], fun cache key -> new IndexBufferBindingResource(cache, key, t, binding))

    member x.CreateIsActive(value : IMod<bool>) =
        isActiveCache.GetOrCreate([value :> obj], fun cache key -> IsActiveResource(cache, key, value))

    interface IDisposable with
        member x.Dispose() = x.Dispose()

type ResourceSet() =
    inherit AdaptiveObject()
    
    let all = ReferenceCountingSet<IResourceLocation>()
    let locked = ReferenceCountingSet<ILockedResource>()
    let dirty = System.Collections.Generic.HashSet<IResourceLocation>()

    member x.AddLocked(l : ILockedResource) =
        lock locked (fun () -> locked.Add l |> ignore)
        
    member x.RemoveLocked(l : ILockedResource) =
        lock locked (fun () -> locked.Remove l |> ignore)

    interface IResourceUser with
        member x.AddLocked l = x.AddLocked l
        member x.RemoveLocked l = x.RemoveLocked l

    override x.InputChanged(_,i) =
        match i with
            | :? IResourceLocation as r -> lock dirty (fun () -> dirty.Add r |> ignore)
            | _ -> ()

    member x.Add(r : IResourceLocation) =
        if all.Add r then
            lock r (fun () ->
                if r.OutOfDate then
                    lock dirty (fun () -> dirty.Add r |> ignore)
                else
                    r.Outputs.Add x |> ignore
            )

    member x.AddAndEvaluate(r : IResourceLocation<'a>) =
        x.EvaluateAlways AdaptiveToken.Top (fun t ->
            all.Add r |> ignore
            r.Update t
        )
     

    member x.Remove(r : IResourceLocation) =
        if all.Remove r then
            lock r (fun () ->
                r.RemoveOutput x
                lock dirty (fun () -> dirty.Remove r |> ignore)
            )

    member x.Update(token : AdaptiveToken) =
        x.EvaluateAlways token (fun token ->
            let rec update () =
                let arr = 
                    lock dirty (fun () -> 
                        let arr = HashSet.toArray dirty
                        dirty.Clear()
                        arr
                    )

                if arr.Length > 0 then
                    let mutable changed = false
                    for r in arr do
                        let info = r.Update(token)
                        changed <- changed || info.version <> -100

                    let rest = update()
                    changed || rest

                else
                    false

            update()
        )

    member x.Use(action : unit -> 'r) =
        let list = lock locked (fun () -> Seq.toArray locked)
        for l in list do l.Lock.Enter(ResourceUsage.Render, l.OnLock)
        try 
            action()
        finally 
            for l in list do l.Lock.Exit(l.OnUnlock)

