#include <metal_stdlib>

using namespace metal;

// Keeps the most recent frame that was not flat.
//
// Writes into a persistent 'keep' texture: nine taps decide whether the incoming frame
// is a flat near-white one, and the shader either takes the new frame or leaves the
// stored one in place. The old value arrives through framebuffer fetch, so no second
// sampled texture and no CPU round trip is needed - and unlike a CPU decision it is made
// for the same frame it applies to, which is what earlier attempts could not do.

struct CopyVertexOut {
    float4 position [[position]];
    float2 uv;
};

struct TexCoords {
    float data[4];
};

struct ConstantBuffers {
    constant TexCoords* tex_coord;
};

struct Textures
{
    texture2d<float, access::sample> texture;
    sampler sampler;
};

struct KeepOut {
    float4 color [[color(0)]];
};

vertex CopyVertexOut vertexMain(uint vid [[vertex_id]],
                                constant ConstantBuffers &constant_buffers [[buffer(CONSTANT_BUFFERS_INDEX)]]) {
    CopyVertexOut out;

    int low = vid & 1;
    int high = vid >> 1;
    out.uv.x = constant_buffers.tex_coord->data[low];
    out.uv.y = constant_buffers.tex_coord->data[2 + high];
    out.position.x = (float(low) - 0.5f) * 2.0f;
    out.position.y = (float(high) - 0.5f) * 2.0f;
    out.position.z = 0.0f;
    out.position.w = 1.0f;

    return out;
}

fragment KeepOut fragmentMain(CopyVertexOut in [[stage_in]],
                              constant Textures &textures [[buffer(TEXTURES_INDEX)]],
                              float4 stored [[color(0)]]) {
    float minLuma = 1e30f;
    float maxLuma = -1e30f;
    float total = 0.0f;

    for (int y = 1; y <= 3; ++y) {
        for (int x = 1; x <= 3; ++x) {
            float3 c = textures.texture.sample(textures.sampler,
                                               float2(float(x) * 0.25f, float(y) * 0.25f)).rgb;
            float luma = dot(c, float3(0.25f, 0.5f, 0.25f));

            minLuma = min(minLuma, luma);
            maxLuma = max(maxLuma, luma);
            total += luma;
        }
    }

    bool flat = (maxLuma - minLuma) <= (2.0f / 255.0f) && (total / 9.0f) >= (240.0f / 255.0f);

    KeepOut out;
    out.color = flat ? stored : textures.texture.sample(textures.sampler, in.uv);

    return out;
}
