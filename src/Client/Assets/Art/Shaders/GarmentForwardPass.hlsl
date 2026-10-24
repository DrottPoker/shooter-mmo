#ifndef MMORPG_GARMENT_FORWARD_PASS_INCLUDED
#define MMORPG_GARMENT_FORWARD_PASS_INCLUDED

// The forward pass of the garment shader: URP Lit's, with the normal turned on the back faces. A
// garment draws both faces, so the inside of a cloak or a skirt faces the camera with the normal of
// its outside; turned, it takes the light that falls on the inside. Include after
// LitForwardPass.hlsl.
void GarmentPassFragment(
    Varyings input
    , FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    input.normalWS = IS_FRONT_VFACE(face, input.normalWS, -input.normalWS);
    LitPassFragment(input, outColor
#ifdef _WRITE_RENDERING_LAYERS
        , outRenderingLayers
#endif
    );
}

#endif
