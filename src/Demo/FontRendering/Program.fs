﻿// Learn more about F# at http://fsharp.org
// See the 'F# Tutorial' project for more help.

open System
open FShade
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.Incremental
open Aardvark.Rendering
open Aardvark.Rendering.NanoVg
open Aardvark.Application
open Aardvark.Application.WinForms
open Aardvark.SceneGraph
open Aardvark.SceneGraph.Semantics
open System.Windows.Media
open System.Windows
open FontRendering
open Aardvark.Rendering.Text

module Shader =
    type Vertex = { 
        [<Position>] pos : V4d 
        [<TexCoord>] tc : V2d
        [<Color>] color : V4d
        [<Semantic("ZZZInstanceTrafo")>] trafo : M44d
    }

    let trafo (v : Vertex) =
        vertex {

            let wp = uniform.ModelTrafo * (v.trafo * v.pos)
            return { 
                pos = uniform.ViewProjTrafo * wp
                tc = v.tc
                trafo = v.trafo
                color = v.color
            }
        }

    let white (v : Vertex) =
        fragment {
            return V4d.IIII
        }


type CameraMode =
    | Orbit
    | Fly
    | Rotate



[<EntryPoint>]
let main argv = 
    Aardvark.Init()


    use app = new OpenGlApplication()
    use win = app.CreateSimpleRenderWindow(16)
    

    let cam = CameraViewWithSky(Location = V3d.III * 2.0, Forward = -V3d.III.Normalized)
    let proj = CameraProjectionPerspective(60.0, 0.1, 1000.0, float win.Width / float win.Height)

    let geometry = 
        IndexedGeometry(
            Mode = IndexedGeometryMode.TriangleList,
            IndexArray = [| 0; 1; 2; 0; 2; 3 |],
            IndexedAttributes =
                SymDict.ofList [
                    DefaultSemantic.Positions,                  [| V3f.OOO; V3f.IOO; V3f.IIO; V3f.OIO |] :> Array
                    DefaultSemantic.DiffuseColorCoordinates,    [| V2f.OO; V2f.IO; V2f.II; V2f.OI |] :> Array
                    DefaultSemantic.Normals,                    [| V3f.OOI; V3f.OOI; V3f.OOI; V3f.OOI |] :> Array
                ]
        )



    let trafos =
        [|
            for x in -4..4 do
                for y in -4..4 do
                    yield Trafo3d.Translation(2.0 * float x - 0.5, 2.0 * float y - 0.5, 0.0)
        |]

    let trafos = trafos |> Mod.constant

    let cam = CameraView.lookAt (V3d.III * 6.0) V3d.Zero V3d.OOI

    let mode = Mod.init Fly
    let controllerActive = Mod.init true

    let flyTo = Mod.init Box3d.Invalid

    let chainM (l : IMod<list<afun<'a, 'a>>>) =
        l |> Mod.map AFun.chain |> AFun.bind id

    let controller (loc : IMod<V3d>) (target : IMod<DateTime * V3d>) = 
        adaptive {
            let! active = controllerActive


            // if the controller is active determine the implementation
            // based on mode
            if active then
                
                let! mode = mode



                return [
                    

                    yield CameraControllers.fly target
                    // scroll and zoom 
                    yield CameraControllers.controlScroll win.Mouse 0.1 0.004
                    yield CameraControllers.controlZoom win.Mouse 0.05

                    
                    match mode with
                        | Fly ->
                            // fly controller special handlers
                            yield CameraControllers.controlLook win.Mouse
                            yield CameraControllers.controlWSAD win.Keyboard 5.0
                            yield CameraControllers.controlPan win.Mouse 0.05

                        | Orbit ->
                            // special orbit controller
                            yield CameraControllers.controlOrbit win.Mouse V3d.Zero

                        | Rotate ->
                            
//                            // rotate is just a regular orbit-controller
//                            // with a simple animation rotating around the Z-Axis
                            yield CameraControllers.controlOrbit win.Mouse V3d.Zero
                            yield CameraControllers.controlAnimation V3d.Zero V3d.OOI

                ]
            else
                // if the controller is inactive simply return an empty-list
                // of controller functions
                return []

        } |> chainM

    let resetPos = Mod.init (6.0 * V3d.III)
    let resetDir = Mod.init (DateTime.MaxValue, V3d.Zero)

    let cam = DefaultCameraController.control win.Mouse win.Keyboard win.Time cam // |> AFun.integrate controller
    //let cam = cam |> AFun.integrate (controller resetPos resetDir)

        
//    let test = sgs |> ASet.map id
//    let r = test.GetReader()
//    r.GetDelta() |> List.length |> printfn "got %d deltas"


    let all = "abcdefghijklmnopqrstuvwxyz\r\nABCDEFGHIJKLMNOPQRSTUVWXYZ\r\n1234567890 ?ß\\\r\n^°!\"§$%&/()=?´`@+*~#'<>|,;.:-_µ"
       
    let md = 
        "# Heading1\r\n" +
        "## Heading2\r\n" + 
        "\r\n" +
        "This is ***markdown*** code being parsed by *CommonMark.Net*  \r\n" +
        "It seems to work quite **well**\r\n" +
        "*italic* **bold** ***bold/italic***\r\n" +
        "\r\n" +
        "    type A(a : int) = \r\n" + 
        "        member x.A = a\r\n" +
        "\r\n" + 
        "regular Text again\r\n"+
        "\r\n" +
        "-----------------------------\r\n" +
        "is there a ruler???\r\n" + 
        "1) First *item*\r\n" + 
        "2) second *item*\r\n" +
        "\r\n"+
        "* First *item*  \r\n" + 
        "with multiple lines\r\n" + 
        "* second *item*\r\n" 

    let message = 
        "# This is Aardvark.Rendering\r\n" +
        "I'm uploading my first screenshot to tracker"
    // old school stuff here^^

    // here's an example-usage of AIR (Aardvark Imperative Renderer) 
    // showing how to integrate arbitrary logic in the SceneGraph without
    // implementing new nodes for that
    let quad = 
        Sg.air { 
            // inside an air-block we're allowed to read current values
            // which will be inherited from the SceneGraph
            let! parentFill = AirState.fillMode

            // modes can be modified by simply calling the respective setters.
            // Note that these setters are overloaded with and without IMod<Mode>
            do! Air.DepthTest    DepthTestMode.LessOrEqual
            do! Air.CullMode     CullMode.None
            do! Air.BlendMode    BlendMode.None
            do! Air.FillMode     FillMode.Fill
            do! Air.StencilMode  StencilMode.Disabled

            // we can also override the shaders in use (and with FSHade)
            // build our own dynamic shaders e.g. depending on the inherited 
            // FillMode from the SceneGraph
            do! Air.BindShader {
                    do! DefaultSurfaces.trafo               
                    
                    // if the parent fillmode is not filled make the quad red.
                    let! fill = parentFill
                    match fill with
                        | FillMode.Fill -> do! DefaultSurfaces.diffuseTexture
                        | _ -> do! DefaultSurfaces.constantColor C4f.Red 

                }

            // uniforms can be bound using lists or one-by-one
            do! Air.BindUniforms [
                    "Hugo", uniformValue 10 
                    "Sepp", uniformValue V3d.Zero
                ]

            do! Air.BindUniform(
                    Symbol.Create "BlaBla",
                    Trafo3d.Identity
                )

            // textures can also be bound (using file-texture here)
            do! Air.BindTexture(
                    DefaultSemantic.DiffuseColorTexture, 
                    @"E:\Development\WorkDirectory\DataSVN\pattern.jpg"
                )

            do! Air.BindVertexBuffers [
                    DefaultSemantic.Positions,                  attValue [|V3f.OOO; V3f.IOO; V3f.IIO; V3f.OIO|]
                    DefaultSemantic.DiffuseColorCoordinates,    attValue [|V2f.OO; V2f.IO; V2f.II; V2f.OI|]
                ]

            do! Air.BindIndexBuffer [| 
                    0;1;2
                    0;2;3 
                |]
        

            // since for some effects it is not desireable to write certain pixel-outputs
            // one can change the current WriteBuffers using a list of written semantics
            do! Air.WriteBuffers [
                    DefaultSemantic.Depth
                    DefaultSemantic.Colors
                ]

            // topology can be set separately (not by the DrawCall)
            do! Air.Toplogy IndexedGeometryMode.TriangleList


            // trafos keep their usual stack-semantics and can be pushed/poped
            // initially the trafo-stack is filled with all trafos inherited 
            // from the containing SceneGraph
            do! Air.PushTrafo (Trafo3d.Scale 5.0)

            // draw the quad 10 times and step by 1/5 in z every time
            for y in 1..10 do
                do! Air.Draw 6
                do! Air.PushTrafo (Trafo3d.Translation(0.0,0.0,1.0/5.0))



        }


    let mode = Mod.init FillMode.Fill
    let font = new Font("Comic Sans")


    let label1 =
        Sg.markdown MarkdownConfig.light (Mod.constant md)
            |> Sg.scale 0.1
            |> Sg.billboard

    let label2 =
        Sg.markdown MarkdownConfig.light (Mod.constant message)
            |> Sg.scale 0.1
            |> Sg.billboard
            |> Sg.translate 5.0 0.0 0.0

    let sg = 
        Sg.group [label1; label2]
            |> Sg.andAlso quad
            |> Sg.viewTrafo (cam |> Mod.map CameraView.viewTrafo)
            |> Sg.projTrafo (win.Sizes |> Mod.map (fun s -> Frustum.perspective 60.0 0.1 100.0 (float s.X / float s.Y) |> Frustum.projTrafo))
            |> Sg.fillMode mode
//            |> Sg.shader {
//                    do! DefaultSurfaces.trafo 
//                    let! mode = mode
//                    match mode with
//                        | FillMode.Fill -> 
//                            do! DefaultSurfaces.diffuseTexture
//                        | _ -> 
//                            do! DefaultSurfaces.constantColor C4f.Red
//               }

    win.Keyboard.KeyDown(Keys.F8).Values.Add (fun _ ->
        transact (fun () ->
            match mode.Value with
                | FillMode.Fill -> mode.Value <- FillMode.Line
                | _ -> mode.Value <- FillMode.Fill
        )
    )

    let main = app.Runtime.CompileRender(win.FramebufferSignature, sg) |> DefaultOverlays.withStatistics
    let clear = app.Runtime.CompileClear(win.FramebufferSignature, Mod.constant C4f.Black)

    win.RenderTask <- RenderTask.ofList [clear; main]
    win.Run()
    0 