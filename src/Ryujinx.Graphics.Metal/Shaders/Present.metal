#include <metal_stdlib>

using namespace metal;

struct CopyVertexOut {
    float4 position [[position]];
    float2 uv;
};

struct PresentParams {
    float data[8];
};

struct ConstantBuffers {
    constant PresentParams* present_params;
};

struct Textures
{
    texture2d<FORMAT, access::sample> texture;
    sampler sampler;
};

vertex CopyVertexOut vertexMain(uint vid [[vertex_id]],
                                constant ConstantBuffers &constant_buffers [[buffer(CONSTANT_BUFFERS_INDEX)]]) {
    CopyVertexOut out;

    int low = vid & 1;
    int high = vid >> 1;
    out.uv.x = constant_buffers.present_params->data[low];
    out.uv.y = constant_buffers.present_params->data[2 + high];
    out.position.x = (float(low) - 0.5f) * 2.0f;
    out.position.y = (float(high) - 0.5f) * 2.0f;
    out.position.z = 0.0f;
    out.position.w = 1.0f;

    return out;
}

fragment FORMAT4 fragmentMain(CopyVertexOut in [[stage_in]],
                              constant ConstantBuffers &constant_buffers [[buffer(CONSTANT_BUFFERS_INDEX)]],
                              constant Textures &textures [[buffer(TEXTURES_INDEX)]]) {
    float2 texel = float2(constant_buffers.present_params->data[4],
                          constant_buffers.present_params->data[5]);
    float sharpness = constant_buffers.present_params->data[6];

    FORMAT4 center = textures.texture.sample(textures.sampler, in.uv);

    if (sharpness <= 0.0f) {
        return center;
    }

    float3 north = float3(textures.texture.sample(textures.sampler, in.uv + float2(0.0f, -texel.y)).rgb);
    float3 south = float3(textures.texture.sample(textures.sampler, in.uv + float2(0.0f, texel.y)).rgb);
    float3 east = float3(textures.texture.sample(textures.sampler, in.uv + float2(texel.x, 0.0f)).rgb);
    float3 west = float3(textures.texture.sample(textures.sampler, in.uv + float2(-texel.x, 0.0f)).rgb);

    float3 blurred = (north + south + east + west) * 0.25f;
    float3 sharpened = clamp(float3(center.rgb) + (float3(center.rgb) - blurred) * sharpness, 0.0f, 1.0f);

    return FORMAT4(sharpened, center.a);
}
