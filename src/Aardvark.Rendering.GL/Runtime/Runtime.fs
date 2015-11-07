﻿namespace Aardvark.Rendering.GL

open System
open System.Collections.Generic
open Aardvark.Base
open Aardvark.Rendering
open OpenTK.Graphics
open Aardvark.Base.Incremental


type ChangeableFramebuffer(c : ChangeableResource<Framebuffer>) =
    let getHandle(caller : IAdaptiveObject) =
        lock c (fun () ->
            if c.OutOfDate then
                c.UpdateCPU(caller)
                c.UpdateGPU(caller) |> ignore
            c.Resource.GetValue(caller) :> IFramebuffer
        )

    interface IFramebuffer with
        member x.GetHandle caller = getHandle(caller).GetHandle(caller)
        member x.Size = getHandle(null).Size
        member x.Attachments = getHandle(null).Attachments
        member x.Dispose() = c.Dispose()

type ChangeableFramebufferTexture(c : ChangeableResource<Texture>) =
    let getHandle(caller : IAdaptiveObject) =
        lock c (fun () ->
            if c.OutOfDate then
                c.UpdateCPU(caller)
                c.UpdateGPU(caller) |> ignore
            c.Resource.GetValue(caller)
        )

    interface IFramebufferTexture with
        member x.GetBackendTexture caller = getHandle(caller) :> ITexture
        member x.GetHandle caller = getHandle(caller).Handle :> obj
        member x.Samples = getHandle(null).Multisamples
        member x.Dimension = getHandle(null).Dimension
        member x.ArraySize = getHandle(null).Count
        member x.MipMapLevels = getHandle(null).MipMapLevels
        member x.GetSize level = getHandle(null).GetSize level
        member x.Dispose() = c.Dispose()
        member x.WantMipMaps = getHandle(null).MipMapLevels > 1
        member x.Download(level) =
            let handle = getHandle(null)
            let format = handle.Format |> TextureFormat.toDownloadFormat
            handle.Context.Download(handle, format, level)

type ChangeableRenderbuffer(c : ChangeableResource<Renderbuffer>) =
    let getHandle(caller : IAdaptiveObject) =
        lock c (fun () ->
            if c.OutOfDate then
                c.UpdateCPU(caller)
                c.UpdateGPU(caller) |> ignore
            c.Resource.GetValue(caller)
        )

    interface IFramebufferRenderbuffer with
        member x.Handle = getHandle(null).Handle :> obj
        member x.Size = getHandle(null).Size
        member x.Samples = getHandle(null).Samples
        member x.Dispose() = c.Dispose()

type ResourceMod<'a, 'b>(res : ChangeableResource<'a>, f : 'a -> 'b) as this =
    inherit AdaptiveObject()
    do res.AddOutput this
       res.Resource.AddOutput this

    member x.GetValue(caller : IAdaptiveObject) =
        x.EvaluateAlways caller (fun () ->
            if res.OutOfDate then
                res.UpdateCPU(x)
                res.UpdateGPU(x) |> ignore
            let r = res.Resource.GetValue(x)
            f r
        )

    member x.Dispose() =
        res.RemoveOutput this
        res.Resource.RemoveOutput this
        res.Dispose()

    interface IMod with
        member x.IsConstant = false
        member x.GetValue(caller) = x.GetValue(caller) :> obj

    interface IMod<'b> with
        member x.GetValue(caller) = x.GetValue(caller)


type Runtime(ctx : Context, shareTextures : bool, shareBuffers : bool) =

    static let versionRx = System.Text.RegularExpressions.Regex @"([0-9]+\.)*[0-9]+"

    let mutable ctx = ctx
    let mutable manager = if ctx <> null then ResourceManager(ctx, shareTextures, shareBuffers) else null

    let resourceMod (f : 'a -> 'b) (a : ChangeableResource<'a>) =
        ResourceMod(a, f) :> IMod<_>

    new(ctx) = new Runtime(ctx, false, false)

    member x.SupportsUniformBuffers =
        ExecutionContext.uniformBuffersSupported

    member x.Context
        with get() = ctx
        and set c = 
            ctx <- c
            manager <- ResourceManager(ctx, shareTextures, shareBuffers)
            //compiler <- Compiler.Compiler(x, c)
            //currentRuntime <- Some (x :> IRuntime)


    member x.Dispose() = 
        if ctx <> null then
            ctx.Dispose()
            ctx <- null
        

    interface IDisposable with
        member x.Dispose() = x.Dispose() 

    interface IRuntime with
        member x.ResolveMultisamples(source, target, trafo) = x.ResolveMultisamples(source, target, trafo)
        member x.ContextLock = ctx.ResourceLock
        member x.CompileRender (engine : BackendConfiguration, set : aset<IRenderObject>) = x.CompileRender(engine,set)
        member x.CompileClear(color, depth) = x.CompileClear(color, depth)
        member x.CreateTexture(size, format, levels, samples) = x.CreateTexture(size, format, levels, samples)
        member x.CreateRenderbuffer(size : IMod<V2i>, format, samples) = x.CreateRenderbuffer(size, format, samples)
        member x.CreateFramebuffer (bindings : Map<Symbol, IMod<_>>) = x.CreateFramebuffer bindings
        
        member x.CreateSurface (s : ISurface) = x.CreateSurface s :> IBackendSurface
        member x.DeleteSurface (s : IBackendSurface) = 
            match s with
                | :? Program as p -> x.DeleteSurface p
                | _ -> failwithf "unsupported program-type: %A" s

        member x.PrepareRenderObject(rj : IRenderObject) = x.PrepareRenderObject rj :> _

        member x.CreateTexture (t : ITexture) = x.CreateTexture t :> IBackendTexture
        member x.DeleteTexture (t : IBackendTexture) =
            match t with
                | :? Texture as t -> x.DeleteTexture t
                | _ -> failwithf "unsupported texture-type: %A" t

        member x.CreateBuffer (b : IBuffer) = x.CreateBuffer b :> IBackendBuffer
        member x.DeleteBuffer (b : IBackendBuffer) = 
            match b with
                | :? Aardvark.Rendering.GL.Buffer as b -> x.DeleteBuffer b
                | _ -> failwithf "unsupported buffer-type: %A" b


        member x.CreateBuffer (b : IMod<IBuffer>) : IMod<IBuffer>=
            manager.CreateBuffer(b) |> resourceMod (fun b -> b :> IBuffer)

        member x.CreateTexture (b : IMod<ITexture>) : IMod<ITexture>=
            manager.CreateTexture(b) |> resourceMod (fun b -> b :> ITexture)

        member x.DeleteBuffer (b : IMod<IBuffer>) =
            match b with
                | :? ResourceMod<Buffer, IBuffer> as r ->
                    r.Dispose()
                | _ ->
                    failwithf "cannot dispose buffer: %A" b

        member x.DeleteTexture (t : IMod<ITexture>) =
            match t with
                | :? ResourceMod<Texture, ITexture> as r ->
                    r.Dispose()
                | _ ->
                    failwithf "cannot dispose texture: %A" t

        member x.DeleteRenderbuffer (b : IRenderbuffer) =
            match b with
                | :? Aardvark.Rendering.GL.Renderbuffer as b -> ctx.Delete b
                | _ -> failwithf "unsupported renderbuffer-type: %A" b

        member x.DeleteFramebuffer(f : IFramebuffer) =
            match f with
                | :? Aardvark.Rendering.GL.Framebuffer as b -> ctx.Delete b
                | _ -> failwithf "unsupported framebuffer-type: %A" f

        member x.CreateStreamingTexture mipMaps = x.CreateStreamingTexture mipMaps
        member x.DeleteStreamingTexture tex = x.DeleteStreamingTexture tex

        member x.CreateFramebuffer(bindings : Map<Symbol, IFramebufferOutput>) : IFramebuffer =
            x.CreateFramebuffer bindings :> _

        member x.CreateTexture(size : V2i, format : TextureFormat, levels : int, samples : int, count : int) : IBackendTexture =
            x.CreateTexture(size, format, levels, samples, count) :> _

        member x.CreateRenderbuffer(size : V2i, format : RenderbufferFormat, samples : int) : IRenderbuffer =
            x.CreateRenderbuffer(size, format, samples) :> IRenderbuffer

    member x.CreateTexture (t : ITexture) = ctx.CreateTexture t
    member x.CreateBuffer (b : IBuffer) : Aardvark.Rendering.GL.Buffer = failwith "not implemented"
    member x.CreateSurface (s : ISurface) = 
        match SurfaceCompilers.compile ctx s with
            | Success prog -> prog
            | Error e -> failwith e

    member x.DeleteTexture (t : Texture) = 
        ctx.Delete t

    member x.DeleteSurface (p : Program) = 
        ctx.Delete p

    member x.DeleteBuffer (b : Aardvark.Rendering.GL.Buffer) =
        ctx.Delete b

    member x.CreateStreamingTexture(mipMaps : bool) =
        ctx.CreateStreamingTexture(mipMaps) :> IStreamingTexture

    member x.DeleteStreamingTexture(t : IStreamingTexture) =
        match t with
            | :? StreamingTexture as t ->
                ctx.Delete(t)
            | _ ->
                failwithf "unsupported streaming texture: %A" t

    member private x.CompileRenderInternal (engine : IMod<BackendConfiguration>, set : aset<IRenderObject>) =
        let eng = engine.GetValue()
        let shareTextures = eng.sharing &&& ResourceSharing.Textures <> ResourceSharing.None
        let shareBuffers = eng.sharing &&& ResourceSharing.Buffers <> ResourceSharing.None
            
        let man = ResourceManager(manager, ctx, shareTextures, shareBuffers)
        new RenderTask(x, ctx, man, engine, set)

    member x.PrepareRenderObject(rj : IRenderObject) =
        match rj with
             | :? RenderObject as rj -> manager.Prepare rj
             | :? PreparedRenderObject -> failwith "tried to prepare prepared render object"
             | _ -> failwith "unknown render object type"

    member x.CompileRender(engine : IMod<BackendConfiguration>, set : aset<IRenderObject>) : IRenderTask =
        x.CompileRenderInternal(engine, set) :> IRenderTask

    member x.CompileRender(engine : BackendConfiguration, set : aset<IRenderObject>) : IRenderTask =
        x.CompileRenderInternal(Mod.constant engine, set) :> IRenderTask

    member x.CompileClear(color : IMod<C4f>, depth : IMod<float>) : IRenderTask =
        new ClearTask(x, color, depth, ctx) :> IRenderTask

    member x.ResolveMultisamples(ms : IFramebufferRenderbuffer, ss : IFramebufferTexture, trafo : ImageTrafo) =
        using ctx.ResourceLock (fun _ ->
            let mutable oldFbo = 0
            OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.FramebufferBinding, &oldFbo);

            match ms.Handle,ss.GetHandle null with
                | (:? int as rb), (:? int as tex) ->
                        
                        
                    let size = ms.Size
                    let readFbo = OpenGL.GL.GenFramebuffer()
                    let drawFbo = OpenGL.GL.GenFramebuffer()

                        
                    OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.ReadFramebuffer,readFbo)
                    OpenGL.GL.FramebufferRenderbuffer(OpenGL.FramebufferTarget.ReadFramebuffer, OpenGL.FramebufferAttachment.ColorAttachment0, OpenGL.RenderbufferTarget.Renderbuffer, rb)

                    OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.DrawFramebuffer,drawFbo)
                    OpenGL.GL.FramebufferTexture(OpenGL.FramebufferTarget.DrawFramebuffer, OpenGL.FramebufferAttachment.ColorAttachment0, tex, 0)

                    let mutable src = Box2i(0, 0, size.X, size.Y)
                    let mutable dst = Box2i(0, 0, size.X, size.Y)

                    match trafo with
                        | ImageTrafo.Rot0 -> ()
                        | ImageTrafo.MirrorY -> 
                            dst.Min.Y <- dst.Max.Y - 1
                            dst.Max.Y <- -1
                        | ImageTrafo.MirrorX ->
                            dst.Min.X <- dst.Max.X - 1
                            dst.Max.X <- -1
                        | _ -> failwith "unsupported image trafo"
                    

                    OpenGL.GL.BlitFramebuffer(src.Min.X, src.Min.Y, src.Max.X, src.Max.Y, dst.Min.X, dst.Min.Y, dst.Max.X, dst.Max.Y, OpenGL.ClearBufferMask.ColorBufferBit, OpenGL.BlitFramebufferFilter.Nearest)

                    OpenGL.GL.FramebufferRenderbuffer(OpenGL.FramebufferTarget.ReadFramebuffer, OpenGL.FramebufferAttachment.ColorAttachment0, OpenGL.RenderbufferTarget.Renderbuffer, 0)
                    OpenGL.GL.FramebufferTexture(OpenGL.FramebufferTarget.DrawFramebuffer, OpenGL.FramebufferAttachment.ColorAttachment0, 0, 0)

                    OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.ReadFramebuffer, 0)
                    OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.DrawFramebuffer, 0)
                    OpenGL.GL.DeleteFramebuffer readFbo
                    OpenGL.GL.DeleteFramebuffer drawFbo

                    OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.Framebuffer,oldFbo)

                | _ -> failwith "not implemented"
        )


    member x.CreateFramebuffer(bindings : Map<Symbol, IFramebufferOutput>) : Framebuffer =

        let depth = Map.tryFind DefaultSemantic.Depth bindings

        let indexed =
            bindings
                |> Map.remove DefaultSemantic.Depth
                |> Map.toList
                |> List.sortBy (fun (s,_) -> 
                    if s = DefaultSemantic.Colors then Int32.MinValue
                    else s.GetHashCode()
                   )
                |> List.mapi (fun i (s,o) -> (i,s,o))

        ctx.CreateFramebuffer(indexed, depth)

    member x.CreateTexture(size : V2i, format : TextureFormat, levels : int, samples : int, count : int) : Texture =
        match count with
            | 1 -> ctx.CreateTexture2D(size, levels, format, samples)
            | _ -> ctx.CreateTexture3D(V3i(size.X, size.Y, count), levels, format, samples)

    member x.CreateRenderbuffer(size : V2i, format : RenderbufferFormat, samples : int) : Renderbuffer =
        ctx.CreateRenderbuffer(size, format, samples)


    member x.CreateFramebuffer(bindings : Map<Symbol, IMod<IFramebufferOutput>>) =
        let fbo = manager.CreateFramebuffer(bindings |> Map.toList)
        new ChangeableFramebuffer(fbo) :> IFramebuffer

    member x.CreateTexture(size : IMod<V2i>, format : IMod<TextureFormat>, mipMaps : IMod<int>, samples : IMod<int>) =
        let tex = manager.CreateTexture(size, mipMaps, format, samples)

        new ChangeableFramebufferTexture(tex) :> IFramebufferTexture

    member x.CreateRenderbuffer(size : IMod<V2i>, format : IMod<RenderbufferFormat>, samples : IMod<int>) =
        let rb = manager.CreateRenderbuffer(size, format, samples)

        new ChangeableRenderbuffer(rb) :> IFramebufferRenderbuffer

    member x.ResolveMultisamples(ms : IFramebufferRenderbuffer, srcRegion : Box2i, ss : IFramebufferTexture, targetRegion : Box2i) =
            using ctx.ResourceLock (fun _ ->
                let mutable oldFbo = 0
                OpenTK.Graphics.OpenGL.GL.GetInteger(OpenTK.Graphics.OpenGL.GetPName.FramebufferBinding, &oldFbo);


                match ms.Handle,ss.GetHandle null with
                    | (:? int as rb), (:? int as tex) ->
                        
                        let size = ms.Size
                        let readFbo = OpenGL.GL.GenFramebuffer()
                        let drawFbo = OpenGL.GL.GenFramebuffer()

                        OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.ReadFramebuffer,readFbo)
                        OpenGL.GL.FramebufferRenderbuffer(OpenGL.FramebufferTarget.ReadFramebuffer, OpenGL.FramebufferAttachment.ColorAttachment0, OpenGL.RenderbufferTarget.Renderbuffer, rb)

                        OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.DrawFramebuffer,drawFbo)
                        OpenGL.GL.FramebufferTexture(OpenGL.FramebufferTarget.DrawFramebuffer, OpenGL.FramebufferAttachment.ColorAttachment0, tex, 0)

                        let src = srcRegion
                        let dst = targetRegion

                        if srcRegion.Size = targetRegion.Size then
                            OpenGL.GL.BlitFramebuffer(src.Min.X, src.Min.Y, src.Max.X, src.Max.Y, dst.Min.X, dst.Min.Y, dst.Max.X, dst.Max.Y, OpenGL.ClearBufferMask.ColorBufferBit, OpenGL.BlitFramebufferFilter.Nearest)
                        else
                            OpenGL.GL.BlitFramebuffer(src.Min.X, src.Min.Y, src.Max.X, src.Max.Y, dst.Min.X, dst.Min.Y, dst.Max.X, dst.Max.Y, OpenGL.ClearBufferMask.ColorBufferBit, OpenGL.BlitFramebufferFilter.Linear)

                        OpenGL.GL.FramebufferRenderbuffer(OpenGL.FramebufferTarget.ReadFramebuffer, OpenGL.FramebufferAttachment.ColorAttachment0, OpenGL.RenderbufferTarget.Renderbuffer, 0)
                        OpenGL.GL.FramebufferTexture(OpenGL.FramebufferTarget.DrawFramebuffer, OpenGL.FramebufferAttachment.ColorAttachment0, 0, 0)

                        OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.ReadFramebuffer, 0)
                        OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.DrawFramebuffer, 0)
                        OpenGL.GL.DeleteFramebuffer readFbo
                        OpenGL.GL.DeleteFramebuffer drawFbo

                        OpenGL.GL.BindFramebuffer(OpenGL.FramebufferTarget.Framebuffer,oldFbo)

                    | _ -> failwith "not implemented"
            )


    new() = new Runtime(null)