// BV1PuuEzaEzj, 03:52: offset = -dir * .05 * (d/r)^8.
// The circle is extended to rounded rectangles using the AndroidLiquidGlass SDF.
// See NOTICE.txt for attribution. This is a WPF port, not Apple's private shader.
sampler2D Backdrop : register(s0);
float4 Size : register(c0);    // panel width, height, corner radius, antialias width (DIP)
float4 Origin : register(c1);  // crop UV origin xy, local X axis in crop UV zw
float4 Axis : register(c2);    // local Y axis in crop UV xy, crop width/height zw
float4 Source : register(c3);  // source width/height in crop UV xy, f, blur DIP
float4 Texel : register(c4);   // half texel in crop UV xy, source DIP blur -> crop UV zw
float4 Optics : register(c5);  // highlight strength, dispersion, light direction xy

float signedDistance(float2 p, float2 halfSize, float radius) {
    float2 q = abs(p) - (halfSize - radius);
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}

float4 sampleBackground(float2 uv) {
    // Keep all taps in the captured source; out-of-bounds UVs must not become black.
    return tex2D(Backdrop, clamp(uv, Texel.xy, 1 - Texel.xy));
}

float4 main(float2 uv : TEXCOORD) : COLOR {
    float2 p = uv * Size.xy;
    float2 centered = p - Size.xy * .5;
    float2 q = abs(centered) - (Size.xy * .5 - Size.z);
    float2 outside = max(q, 0);
    float2 cornerNormal = outside / max(length(outside), .00001);
    float2 faceNormal = q.x > q.y ? float2(1, 0) : float2(0, 1);
    float2 normal = (max(q.x, q.y) > 0 ? cornerNormal : faceNormal) * sign(centered);
    float distance = signedDistance(centered, Size.xy * .5, Size.z);
    float ratio = saturate(1 + distance / max(Size.z, .00001));
    float square = ratio * ratio;
    float fourth = square * square;
    float eighth = fourth * fourth;

    // Rotate/reflect the local normal into SOURCE coordinates before UV displacement.
    // Multiplying crop UV axes by crop size recovers the local-to-source DIP matrix.
    float2 sourceNormal = (Origin.zw * normal.x + Axis.xy * normal.y) * Axis.zw;
    sourceNormal /= max(length(sourceNormal), .00001);
    float2 center = Origin.xy + Origin.zw * p.x + Axis.xy * p.y;
    center -= sourceNormal * Source.xy * Source.z * eighth;

    // Video leaves blur() unspecified. Use a fixed, normalized nine-tap Gaussian.
    float2 tap = Texel.zw * Source.w;
    float4 color = sampleBackground(center) * .25;
    color += (sampleBackground(center + float2(tap.x, 0)) + sampleBackground(center - float2(tap.x, 0))
            + sampleBackground(center + float2(0, tap.y)) + sampleBackground(center - float2(0, tap.y))) * .125;
    color += (sampleBackground(center + tap) + sampleBackground(center - tap)
            + sampleBackground(center + float2(tap.x, -tap.y)) + sampleBackground(center + float2(-tap.x, tap.y))) * .0625;

    // A small spectral separation is confined to the refracting perimeter.
    // Keep the blurred center intact and preserve premultiplied source alpha.
    float2 spectral = sourceNormal * Texel.zw * Optics.y * 2 * eighth;
    float4 redSample = sampleBackground(center + spectral);
    float4 blueSample = sampleBackground(center - spectral);
    float spectralMix = .35 * eighth * Optics.y;
    color.r = lerp(color.r, min(redSample.r, color.a), spectralMix);
    color.b = lerp(color.b, min(blueSample.b, color.a), spectralMix);

    // Thin specular glints follow the SDF normal; no broad white polygon is drawn.
    float light = dot(normal, normalize(Optics.zw));
    float rim = exp2(-abs(distance) * 1.6);
    float specular = (pow(saturate(light), 4) * .42 * rim
        + pow(saturate(-light), 6) * .16 * rim
        + square * .045) * Optics.x;
    color.rgb *= 1 - saturate(-light) * eighth * .08 * Optics.x;
    color.rgb = lerp(color.rgb, color.aaa, specular);
    float coverage = saturate(.5 - distance / max(Size.w, .25));
    // WPF sampler colors are premultiplied: retain source alpha through blur and coverage.
    return color * coverage;
}
