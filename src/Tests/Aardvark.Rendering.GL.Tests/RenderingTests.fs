﻿namespace Aardvark.Rendering.GL.Tests

open System
open NUnit.Framework
open FsUnit
open Aardvark.Rendering.GL
open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.SceneGraph
open Aardvark.Base.Incremental.Operators

module Vector =

    let inline ofArray (arr : 'a[]) =
        Vector<'a>(arr)

    let inline ofSeq (s : seq<'a>) =
        s |> Seq.toArray |> ofArray

    let inline ofList (s : list<'a>) =
        s |> List.toArray |> ofArray

    let inline toSeq (v : Vector<'a>) =
        v.Data :> seq<_>

    let inline toList (v : Vector<'a>) =
        v.Data |> Array.toList

    let inline toArray (v : Vector<'a>) =
        v.Data

    let init (size : int) (f : int -> 'a) =
        Vector<'a>(Array.init size f)

    let create (size : int) (value : 'a) =
        Vector<'a>(Array.create size value)

    let zeroCreate (size : int) =
        Vector<'a>(Array.zeroCreate size)

    let inline map (f : 'a -> 'b) (v : Vector<'a>) =
        v.Map(f)

    let inline map2 (f : 'a -> 'b -> 'c) (l : Vector<'a>) (r : Vector<'b>) =
        let res = Vector<'c>(min l.Size r.Size)
        res.SetMap2(l, r, f)

    let inline fold (f : 's -> 'a -> 's) (seed : 's) (v : Vector<'a>) =
        v.Norm(id, seed, f)

    let inline fold2 (f : 's -> 'a -> 'b -> 's) (seed : 's) (a : Vector<'a>) (b : Vector<'b>) =
        a.InnerProduct(b, (fun a b -> (a,b)), seed, fun s (a,b) -> f s a b)

    let inline dot (l : Vector<'a>) (r : Vector<'b>) : 'c =
        l.InnerProduct(r, (*), LanguagePrimitives.GenericZero, (+))

    let inline normSquared (v : Vector<'a>) : 'b =
        v.Norm((fun a -> a * a), LanguagePrimitives.GenericZero, (+))

    let inline norm (v : Vector<'a>) : 'b =
        sqrt (normSquared v)

module Matrix =

    let init (size : V2i) (f : V2i -> 'a) =
        Matrix<'a>(size).SetByCoord(fun (c : V2l) -> f (V2i c))
   
    let inline fold (f : 's -> 'a -> 's) (seed : 's) (m : Matrix<'a>) =
        m.Norm(id, seed, f)

    let inline fold2 (f : 's -> 'a -> 'b -> 's) (seed : 's) (a : Matrix<'a>) (b : Matrix<'b>) =
        a.InnerProduct(b, (fun a b -> a,b), seed, fun s (a,b) -> f s a b)

    let inline equal (l : Matrix<'a>) (r : Matrix<'a>) =
        l.InnerProduct(r, (=), true, (&&), not)

    let inline notEqual (l : Matrix<'a>) (r : Matrix<'a>) =
        equal l r |> not

[<AutoOpen>]
module TensorSlices =
    
    type Vector<'a> with
        member x.GetSlice(first : Option<int>, last : Option<int>) =
            let first = defaultArg first 0
            let last = defaultArg last (int x.Size - 1)

            x.SubVector(first, 1 + last - first)

        member x.SetSlice(first : Option<int>, last : Option<int>, value : 'a) =
            x.GetSlice(first, last).Set(value) |> ignore

    type Vector<'a, 'b> with
        member x.GetSlice(first : Option<int>, last : Option<int>) =
            let first = defaultArg first 0
            let last = defaultArg last (int x.Size - 1)

            x.SubVector(first, 1 + last - first)

        member x.SetSlice(first : Option<int>, last : Option<int>, value : 'b) =
            x.GetSlice(first, last).Set(value) |> ignore


    type Matrix<'a> with

        member x.GetSlice(first : Option<V2i>, last : Option<V2i>) =
            let first = defaultArg first V2i.Zero
            let last = defaultArg last (V2i x.Size - V2i.II)

            x.SubMatrix(first, V2i.II + last - first)

        member x.GetSlice(xStart : Option<int>, xEnd : Option<int>, yStart : Option<int>, yEnd : Option<int>) =
            let xStart = defaultArg xStart 0
            let xEnd = defaultArg xEnd (int x.Size.X - 1)
            let yStart = defaultArg yStart 0
            let yEnd = defaultArg yEnd (int x.Size.Y - 1)
            
            let p0 = V2i(xStart, yStart)
            let size = V2i(xEnd + 1, yEnd + 1) - p0

            x.SubMatrix(p0, size)

        member m.GetSlice(x : int, yStart : Option<int>, yEnd : Option<int>) =
            m.SubYVector(int64 x).GetSlice(yStart, yEnd)

        member x.GetSlice(xStart : Option<int>, xEnd : Option<int>, y : int) =
            x.SubXVector(int64 y).GetSlice(xStart, xEnd)

    type Matrix<'a, 'b> with

        member x.GetSlice(first : Option<V2i>, last : Option<V2i>) =
            let first = defaultArg first V2i.Zero
            let last = defaultArg last (V2i x.Size - V2i.II)

            x.SubMatrix(first, V2i.II + last - first)

        member x.GetSlice(xStart : Option<int>, xEnd : Option<int>, yStart : Option<int>, yEnd : Option<int>) =
            let xStart = defaultArg xStart 0
            let xEnd = defaultArg xEnd (int x.Size.X - 1)
            let yStart = defaultArg yStart 0
            let yEnd = defaultArg yEnd (int x.Size.Y - 1)
            
            let p0 = V2i(xStart, yStart)
            let size = V2i(xEnd + 1, yEnd + 1) - p0

            x.SubMatrix(p0, size)

        member m.GetSlice(x : int, yStart : Option<int>, yEnd : Option<int>) =
            m.SubYVector(int64 x).GetSlice(yStart, yEnd)

        member x.GetSlice(xStart : Option<int>, xEnd : Option<int>, y : int) =
            x.SubXVector(int64 y).GetSlice(xStart, xEnd)


    type Volume<'a> with
        member v.GetSlice(x : int, yStart : Option<int>, yEnd : Option<int>, zStart : Option<int>, zEnd : Option<int>) =
            v.SubYZMatrix(int64 x).GetSlice(yStart, yEnd, zStart, zEnd)

        member v.GetSlice(xStart : Option<int>, xEnd : Option<int>, y : int, zStart : Option<int>, zEnd : Option<int>) =
            v.SubXZMatrix(int64 y).GetSlice(xStart, xEnd, zStart, zEnd)

        member v.GetSlice(xStart : Option<int>, xEnd : Option<int>, yStart : Option<int>, yEnd : Option<int>, z : int) =
            v.SubXYMatrix(int64 z).GetSlice(xStart, xEnd, yStart, yEnd)

        member v.GetSlice(xStart : Option<int>, xEnd : Option<int>, y : int, z : int) =
            v.SubXYMatrix(int64 z).SubXVector(int64 y).GetSlice(xStart, xEnd)

        member v.GetSlice(x : int, yStart : Option<int>, yEnd : Option<int>, z : int) =
            v.SubXYMatrix(int64 z).SubYVector(int64 x).GetSlice(yStart, yEnd)

        member v.GetSlice(x : int, y : int, zStart : Option<int>, zEnd : Option<int>) =
            v.SubYZMatrix(int64 x).SubYVector(int64 y).GetSlice(zStart, zEnd)

        member v.GetSlice(xStart : Option<int>, xEnd : Option<int>, yStart : Option<int>, yEnd : Option<int>, zStart : Option<int>, zEnd : Option<int>) =
            let xStart = defaultArg xStart 0
            let xEnd = defaultArg xEnd (int v.Size.X - 1)
            let yStart = defaultArg yStart 0
            let yEnd = defaultArg yEnd (int v.Size.Y - 1)
            let zStart = defaultArg zStart 0
            let zEnd = defaultArg zEnd (int v.Size.Z - 1)
            
            let p0 = V3i(xStart, yStart, zStart)
            let s = V3i(1 + xEnd, 1 + yEnd, 1 + zEnd) - p0

            v.SubVolume(p0, s)

    type Volume<'a, 'b> with
        member v.GetSlice(x : int, yStart : Option<int>, yEnd : Option<int>, zStart : Option<int>, zEnd : Option<int>) =
            v.SubYZMatrix(int64 x).GetSlice(yStart, yEnd, zStart, zEnd)

        member v.GetSlice(xStart : Option<int>, xEnd : Option<int>, y : int, zStart : Option<int>, zEnd : Option<int>) =
            v.SubXZMatrix(int64 y).GetSlice(xStart, xEnd, zStart, zEnd)

        member v.GetSlice(xStart : Option<int>, xEnd : Option<int>, yStart : Option<int>, yEnd : Option<int>, z : int) =
            v.SubXYMatrix(int64 z).GetSlice(xStart, xEnd, yStart, yEnd)

        member v.GetSlice(xStart : Option<int>, xEnd : Option<int>, y : int, z : int) =
            v.SubXYMatrix(int64 z).SubXVector(int64 y).GetSlice(xStart, xEnd)

        member v.GetSlice(x : int, yStart : Option<int>, yEnd : Option<int>, z : int) =
            v.SubXYMatrix(int64 z).SubYVector(int64 x).GetSlice(yStart, yEnd)

        member v.GetSlice(x : int, y : int, zStart : Option<int>, zEnd : Option<int>) =
            v.SubYZMatrix(int64 x).SubYVector(int64 y).GetSlice(zStart, zEnd)

        member v.GetSlice(xStart : Option<int>, xEnd : Option<int>, yStart : Option<int>, yEnd : Option<int>, zStart : Option<int>, zEnd : Option<int>) =
            let xStart = defaultArg xStart 0
            let xEnd = defaultArg xEnd (int v.Size.X - 1)
            let yStart = defaultArg yStart 0
            let yEnd = defaultArg yEnd (int v.Size.Y - 1)
            let zStart = defaultArg zStart 0
            let zEnd = defaultArg zEnd (int v.Size.Z - 1)
            
            let p0 = V3i(xStart, yStart, zStart)
            let s = V3i(1 + xEnd, 1 + yEnd, 1 + zEnd) - p0

            v.SubVolume(p0, s)



module RenderingTests =
    
    do Aardvark.Init()

    let quad = 
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


    [<Test>]
    let ``[GL] simple render to texture``() =
        
        let vec = Vector.zeroCreate 1000
        vec.[0..9] <- 1.0
        
        let len = vec |> Vector.norm

        printfn "%A" len

        use runtime = new Runtime()
        use ctx = new Context(runtime)
        runtime.Context <- ctx

        let size = V2i(1024,768)
        let color = runtime.CreateTexture(~~size, ~~PixFormat.ByteBGRA, ~~1, ~~1)
        let depth = runtime.CreateRenderbuffer(~~size, ~~RenderbufferFormat.Depth24Stencil8, ~~1)


        let fbo = 
            runtime.CreateFramebuffer(
                Map.ofList [
                    DefaultSemantic.Colors, ~~({ texture = color; slice = 0; level = 0 } :> IFramebufferOutput)
                    DefaultSemantic.Depth, ~~(depth :> IFramebufferOutput)
                ]
            )


        
        let sg =
            quad 
                |> Sg.ofIndexedGeometry
                |> Sg.effect [DefaultSurfaces.trafo |> toEffect; DefaultSurfaces.constantColor C4f.White |> toEffect]

        
        use task = runtime.CompileRender(sg)
        use clear = runtime.CompileClear(~~C4f.Black, ~~1.0)

        clear.Run(fbo) |> ignore
        task.Run(fbo) |> ignore

        let pi = color.Download(0).[0].ToPixImage<byte>(Col.Format.BGRA)

        let cmp = PixImage<byte>(Col.Format.BGRA, size)
        cmp.GetMatrix<C4b>().SetByCoord(fun (c : V2l) ->
            if c.X >= int64 size.X / 2L && c.Y >= int64 size.Y / 2L then
                C4b.White
            else
                C4b.Black
        ) |> ignore




        pi.SaveAsImage @"C:\Users\haaser\Desktop\test.png"


        ()

