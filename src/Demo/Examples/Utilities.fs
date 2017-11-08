﻿namespace Examples

open System
open Aardvark.Base
open Aardvark.Base.Incremental

open Aardvark.SceneGraph
open Aardvark.Application
open Aardvark.Application.WinForms
open Aardvark.Base.Incremental.Operators
open Aardvark.Base.Rendering
open Aardvark.Base.ShaderReflection
open Aardvark.Rendering.Vulkan
open Aardvark.Rendering.Text
open Aardvark.Application.OpenVR

[<RequireQualifiedAccess>]
type Backend =
    | GL 
    | Vulkan 
    
[<RequireQualifiedAccess>]
type Display =
    | Mono
    | Stereo
    | OpenVR

module Utilities =
    open System.Windows.Forms


    [<AbstractClass>]
    type OutputMod<'a, 'b>(inputs : list<IOutputMod>) =
        inherit AbstractOutputMod<'b>()

        let mutable handle : Option<'a> = None

        abstract member View : 'a -> 'b
        default x.View a = unbox a
        
        abstract member TryUpdate : AdaptiveToken * 'a -> bool
        default x.TryUpdate(_,_) = false

        abstract member Create : AdaptiveToken -> 'a
        abstract member Destroy : 'a -> unit

        override x.Create() =
            for i in inputs do i.Acquire()

        override x.Destroy() =
            for i in inputs do i.Release()
            match handle with
                | Some h -> 
                    x.Destroy h
                    handle <- None
                | _ ->
                    ()

        override x.Compute(t, rt) =
            let handle = 
                match handle with
                    | Some h ->
                        if not (x.TryUpdate(t, h)) then
                            x.Destroy(h)
                            let h = x.Create(t)
                            handle <- Some h
                            h
                        else
                            h
                    | None ->
                        let h = x.Create t
                        handle <- Some h
                        h
            x.View handle
                    
    module OutputMod =
        let custom (dependent : list<IOutputMod>) (create : AdaptiveToken -> 'a) (tryUpdate : AdaptiveToken -> 'a -> bool) (destroy : 'a -> unit) (view : 'a -> 'b) =
            { new OutputMod<'a, 'b>(dependent) with
                override x.Create t = create t
                override x.TryUpdate(t,h) = tryUpdate t h
                override x.Destroy h = destroy h
                override x.View h = view h
            } :> IOutputMod<_> 
            
        let simple (create : AdaptiveToken -> 'a) (destroy : 'a -> unit) =
            { new OutputMod<'a, 'a>([]) with
                override x.Create t = create t
                override x.Destroy h = destroy h
            } :> IOutputMod<_>

    module private Shader =
        open FShade

        let stereoTexture =
            sampler2dArray {
                texture uniform?DiffuseColorTexture
                filter Filter.MinMagLinear
                addressU WrapMode.Wrap
                addressV WrapMode.Wrap
            }

        let renderStereo (v : Effects.Vertex) =
            fragment {
                let index =
                    if v.tc.X > 0.5 then 1
                    else 0

                let tc =
                    if v.tc.X > 0.5 then V2d((v.tc.X - 0.5) * 2.0, v.tc.Y)
                    else V2d(v.tc.X * 2.0, v.tc.Y)

                return stereoTexture.SampleLevel(tc, index, uniform?Dependent)
            }


    let private runMonoScreen (backend : Backend) (scene : ISg) =
        let app =
            match backend with
                | Backend.GL -> new OpenGlApplication(true) :> IApplication
                | Backend.Vulkan -> new VulkanApplication(true) :> IApplication

        let win = app.CreateSimpleRenderWindow(8)

        let view =
            CameraView.lookAt (V3d(6,6,6)) V3d.Zero V3d.OOI
                |> DefaultCameraController.control win.Mouse win.Keyboard win.Time
                |> Mod.map CameraView.viewTrafo

        let proj =
            win.Sizes 
                |> Mod.map (fun s -> Frustum.perspective 60.0 0.1 1000.0 (float s.X / float s.Y))
                |> Mod.map Frustum.projTrafo

        let task =
            scene
            |> Sg.viewTrafo view
            |> Sg.projTrafo proj
            |> Sg.compile win.Runtime win.FramebufferSignature

        win.RenderTask <- task
        win.Run()

        task.Dispose()
        win.Dispose()
        app.Dispose()


    let private runStereoScreen (backend : Backend) (scene : ISg) =
        let app =
            match backend with
                | Backend.GL -> new OpenGlApplication(true) :> IApplication
                | Backend.Vulkan -> new VulkanApplication(true) :> IApplication

        let win = app.CreateSimpleRenderWindow(1)
        let runtime = app.Runtime

        let samples = 8
        let signature =
            runtime.CreateFramebufferSignature(
                SymDict.ofList [
                    DefaultSemantic.Colors, { format = RenderbufferFormat.Rgba8; samples = samples }
                    DefaultSemantic.Depth, { format = RenderbufferFormat.Depth24Stencil8; samples = samples }
                ],
                Set.empty,
                2, 
                Set.ofList [
                    "ProjTrafo"; 
                    "ViewTrafo"; 
                    "ModelViewTrafo"; 
                    "ViewProjTrafo"; 
                    "ModelViewProjTrafo"
                    
                    "ProjTrafoInv"; 
                    "ViewTrafoInv"; 
                    "ModelViewTrafoInv"; 
                    "ViewProjTrafoInv"; 
                    "ModelViewProjTrafoInv"
                ]
            )  

        let s = win.Sizes |> Mod.map (fun s -> s / V2i(2,1))

        let colors =
            OutputMod.custom 
                []
                (fun t -> runtime.CreateTextureArray(s.GetValue t, TextureFormat.Rgba8, 1, samples, 2))
                (fun t h -> h.Size.XY = s.GetValue t)
                (fun h -> runtime.DeleteTexture h)
                id
                
        let depth =
            OutputMod.custom 
                []
                (fun t -> runtime.CreateTextureArray(s.GetValue t, TextureFormat.Depth24Stencil8, 1, samples, 2))
                (fun t h -> h.Size.XY = s.GetValue t)
                (fun h -> runtime.DeleteTexture h)
                id

        let resolved =
            OutputMod.custom 
                []
                (fun t -> runtime.CreateTextureArray(s.GetValue t, TextureFormat.Rgba8, 1, 1, 2))
                (fun t h -> h.Size.XY = s.GetValue t)
                (fun h -> runtime.DeleteTexture h)
                id
                
        let framebuffer =
            OutputMod.custom
                [colors; depth]
                (fun t -> runtime.CreateFramebuffer(signature, [DefaultSemantic.Colors, colors.GetValue(t).[0] :> IFramebufferOutput; DefaultSemantic.Depth, depth.GetValue(t).[0] :> IFramebufferOutput]))
                (fun t h -> false)
                (fun h -> runtime.DeleteFramebuffer h)
                id


        let view = 
            CameraView.lookAt (V3d(6.0, 6.0, 6.0)) V3d.Zero V3d.OOI
                |> DefaultCameraController.control win.Mouse win.Keyboard win.Time
                |> Mod.map CameraView.viewTrafo

        let near = 0.1
        let far = 1000.0

        let views =
            view |> Mod.map (fun view ->
                [| 
                    view * Trafo3d.Translation(0.05, 0.0, 0.0)
                    view * Trafo3d.Translation(-0.05, 0.0, 0.0)
                |]
            )

        let projs =
            // taken from oculus rift
            let outer = 1.0537801252809621805875367233154
            let inner = 0.77567951104961310377955052031392

            win.Sizes |> Mod.map (fun size ->
                let aspect = float size.X / float size.Y 
                let y = tan (120.0 * Constant.RadiansPerDegree / 2.0) / aspect //(outer + inner) / (2.0 * aspect)

                [|
                    { left = -outer * near; right = inner * near; top = y * near; bottom = -y * near; near = near; far = far } |> Frustum.projTrafo 
                    { left = -inner * near; right = outer * near; top = y * near; bottom = -y * near; near = near; far = far } |> Frustum.projTrafo 
                |]
            )

        let stereoTask =
            scene
            |> Sg.uniform "ViewTrafo" views
            |> Sg.uniform "ProjTrafo" projs
            |> Sg.viewTrafo view
            |> Sg.uniform "CameraLocation" (view |> Mod.map (fun t -> t.Backward.C3.XYZ))
            |> Sg.uniform "LightLocation" (view |> Mod.map (fun t -> t.Backward.C3.XYZ))
            |> Sg.compile runtime signature

        let clearTask =
            runtime.CompileClear(signature, ~~C4f.Black, ~~1.0)

        let dependent =
            Mod.custom (fun t ->
                let fbo = framebuffer.GetValue t
                let output = OutputDescription.ofFramebuffer fbo

                clearTask.Run(t, RenderToken.Empty, output)
                stereoTask.Run(t, RenderToken.Empty, output)
                runtime.Copy(colors.GetValue(t), 0, 0, resolved.GetValue(t), 0, 0, 2, 1)

                0.0
            )

        let task =
            Sg.fullScreenQuad
                |> Sg.uniform "Dependent" dependent
                |> Sg.diffuseTexture (resolved |> Mod.map (fun t -> t :> ITexture))
                |> Sg.shader {
                    do! Shader.renderStereo
                }
                |> Sg.compile runtime win.FramebufferSignature

        win.RenderTask <- task
        framebuffer.Acquire()
        resolved.Acquire()

        win.Run()

        framebuffer.Release()
        resolved.Release()

        task.Dispose()
        clearTask.Dispose()
        stereoTask.Dispose()

        win.Dispose()
        runtime.DeleteFramebufferSignature signature
        
        app.Dispose()

    let private runOpenVR (backend : Backend) (scene : ISg) =
        match backend with
            | Backend.Vulkan ->
                let app = VulkanVRApplicationLayered(8, true)

                let hmdLocation = app.Hmd.MotionState.Pose |> Mod.map (fun t -> t.Forward.C3.XYZ)

                let sg =
                    scene
                    |> Sg.uniform "ViewTrafo" app.Info.viewTrafos
                    |> Sg.uniform "ProjTrafo" app.Info.projTrafos
                    |> Sg.uniform "CameraLocation" hmdLocation
                    |> Sg.uniform "LightLocation" hmdLocation

                app.RenderTask <- app.Runtime.CompileRender(app.FramebufferSignature, scene)
                app.Run()
            | Backend.GL -> 
                failwith "no OpenGL OpenVR backend atm."

    let run (display : Display) (backend : Backend) (scene : ISg) =
        match display with
            | Display.Mono -> runMonoScreen backend scene
            | Display.Stereo -> runStereoScreen backend scene
            | Display.OpenVR -> runOpenVR backend scene