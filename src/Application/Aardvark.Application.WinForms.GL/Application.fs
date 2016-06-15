﻿namespace Aardvark.Application.WinForms

open System
open System.Windows.Forms

open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.Rendering.GL
open Aardvark.Application


type OpenGlApplication() =
    do OpenTK.Toolkit.Init(new OpenTK.ToolkitOptions(Backend=OpenTK.PlatformBackend.PreferNative)) |> ignore
       try 
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException)
       with e -> Report.Warn("Could not set SetUnhandledExceptionMode.")

    let runtime = new Runtime()
    let ctx = new Context(runtime)
    do runtime.Context <- ctx
       
    let init =
        let initialized = ref false
        fun (ctx : Context)  ->
            if not !initialized then
                initialized := true
//                glctx.MakeCurrent(w)
//                glctx.LoadAll()
//                match glctx with | :? OpenTK.Graphics.IGraphicsContextInternal as c -> c.GetAddress("glBindFramebuffer") |> ignore | _ -> ()
//
//                let handle = ContextHandle(glctx,w)
//                ctx.CurrentContextHandle <- Some handle
//                ContextHandle.Current <- Some handle

                using ctx.ResourceLock (fun _ ->

                    Log.startTimed "initializing OpenGL runtime"

//                    Aardvark.Rendering.GL.OpenGl.Unsafe.BindFramebuffer (int OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer) 0
//                    OpenTK.Graphics.OpenGL4.GL.GetError() |> ignore
//                    OpenTK.Graphics.OpenGL4.GL.BindFramebuffer(OpenTK.Graphics.OpenGL4.FramebufferTarget.Framebuffer, 0)
//                    OpenTK.Graphics.OpenGL4.GL.Check "first GL call failed"
                    OpenGl.Unsafe.ActiveTexture (int OpenTK.Graphics.OpenGL4.TextureUnit.Texture0)
                    OpenTK.Graphics.OpenGL4.GL.Check "first GL call failed"
                
                    try GLVM.vmInit()
                    with _ -> Log.line "No glvm found, running without glvm"
               
                    Log.line "vendor:   %A" ctx.Driver.vendor
                    Log.line "renderer: %A" ctx.Driver.renderer 
                    Log.line "version:  OpenGL %A / GLSL %A" ctx.Driver.version ctx.Driver.glsl

                    Log.stop()
                )

    do init ctx

//                glctx.MakeCurrent(null)
//                ctx.CurrentContextHandle <- None
//                ContextHandle.Current <- None

    member x.Context = ctx
    member x.Runtime = runtime

    member x.Dispose() =
        ctx.Dispose()
        runtime.Dispose()

    member x.Initialize(ctrl : IRenderControl, samples : int) = 
        match ctrl with
            | :? RenderControl as ctrl ->
                
                ctrl.Implementation <- new OpenGlRenderControl(runtime, samples)
                init ctx 
            | _ ->
                failwithf "unknown control type: %A" ctrl
        

    member x.CreateGameWindow(?samples : int) =
        let samples = defaultArg samples 1
        let w = new GameWindow(runtime, samples)
        init ctx 
        w

    interface IApplication with
        member x.Initialize(ctrl : IRenderControl, samples : int) = x.Initialize(ctrl, samples)
        member x.Runtime = x.Runtime :> IRuntime
        member x.Dispose() = x.Dispose()

