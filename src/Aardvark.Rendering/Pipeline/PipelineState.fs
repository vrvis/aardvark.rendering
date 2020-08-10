﻿namespace Aardvark.Rendering

open System
open FSharp.Data.Adaptive
open Aardvark.Base

open Aardvark.Rendering

type PipelineState =
    {
        depthTest           : aval<DepthTestMode>
        depthBias           : aval<DepthBias>
        cullMode            : aval<CullMode>
        frontFace           : aval<WindingOrder>
        blendMode           : aval<BlendMode>
        fillMode            : aval<FillMode>
        stencilMode         : aval<StencilMode>
        multisample         : aval<bool>
        writeBuffers        : Option<Set<Symbol>>
        globalUniforms      : IUniformProvider

        geometryMode        : IndexedGeometryMode
        vertexInputTypes    : Map<Symbol, Type>
        perGeometryUniforms : Map<string, Type>
    }
