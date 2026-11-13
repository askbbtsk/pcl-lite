// Apple WWDC25 Dual-Zone Optical Liquid Glass Shader (WPF ps_3_0)
// Combines smooth inner Gaussian frost (s1) with crisp dome-beveled rim refraction &
// chromatic lensing (s0), diffuse skylight scatter, subsurface caustics, and Screen-blended
// corner catchlights. See NOTICE.txt for AndroidLiquidGlass SDF attribution.
sampler2D Backdrop : register(s0);
sampler2D FrostedBackdrop : register(s1);
float4 Size : register(c0);      // panel width, height, corner radius, antialias width (DIP)
float4 Origin : register(c1);    // crop UV origin xy, local X axis in crop UV zw
float4 Axis : register(c2);      // local Y axis in crop UV xy, crop width/height zw
float4 Source : register(c3);    // source width/height in crop UV xy, f, blur DIP
float4 Texel : register(c4);     // half texel in crop UV xy, source DIP blur -> crop UV zw
float4 Optics : register(c5);    // highlight strength, dispersion, light direction xy
float4 Tint : register(c6);      // theme tint RGB (0..1), wash opacity (0..1)
float4 Material : register(c7);  // diffuse strength, edge sharpness, hasFrost (0/1), isDarkMode (0/1)

float signedDistance(float2 p, float2 halfSize, float radius) {
    float2 q = abs(p) - (halfSize - radius);
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}

float4 sampleClamped(sampler2D tex, float2 uv) {
    return tex2D(tex, clamp(uv, Texel.xy, 1 - Texel.xy));
}

float4 main(float2 uv : TEXCOORD) : COLOR {
    float2 p = uv * Size.xy;
    float2 halfSize = Size.xy * .5;
    float2 centered = p - halfSize;
    float2 q = abs(centered) - (halfSize - Size.z);
    float2 outside = max(q, 0);
    float2 cornerNormal = outside / max(length(outside), .00001);
    float2 faceNormal = q.x > q.y ? float2(1, 0) : float2(0, 1);
    float2 normal = (max(q.x, q.y) > 0 ? cornerNormal : faceNormal) * sign(centered);
    float distance = signedDistance(centered, halfSize, Size.z);

    // Wide smooth optical dome bevel + subtle interior pillow bulge (prevents flat acrylic look)
    float bevel = min(max(Size.z * 1.45, 22.0), min(Size.x, Size.y) * .48);
    float ratio = saturate(1.0 + distance / max(bevel, .0001));
    float dome = (ratio / sqrt(max(1.0 - .88 * ratio * ratio, .04))) * .3125;

    float2 sourceNormal = (Origin.zw * normal.x + Axis.xy * normal.y) * Axis.zw;
    sourceNormal /= max(length(sourceNormal), .00001);

    float2 ndc = centered / max(halfSize, .001);
    float r2 = min(dot(ndc, ndc), 2.0);
    float2 pillow = ndc * (1.0 - .45 * r2);
    float2 sourcePillow = (Origin.zw * pillow.x + Axis.xy * pillow.y) * Axis.zw;

    float2 baseUv = Origin.xy + Origin.zw * p.x + Axis.xy * p.y;
    float2 dispScale = Source.xy * Source.z;
    float2 sampleUv = baseUv - sourceNormal * dispScale * (dome * .78) - sourcePillow * dispScale * .06;

    // 1. Frosted interior sample (GPU BlurEffect in s1 + balanced 5-tap preset blur from Source.w)
    float2 tap = Texel.zw * Source.w * .85;
    float4 frosted = sampleClamped(FrostedBackdrop, sampleUv) * .36;
    frosted += (sampleClamped(FrostedBackdrop, sampleUv + float2(tap.x, 0))
              + sampleClamped(FrostedBackdrop, sampleUv - float2(tap.x, 0))
              + sampleClamped(FrostedBackdrop, sampleUv + float2(0, tap.y))
              + sampleClamped(FrostedBackdrop, sampleUv - float2(0, tap.y))) * .16;

    // 2. Crisp beveled lens sample (s0) + subtle chromatic edge dispersion
    float4 sharp = sampleClamped(Backdrop, sampleUv);
    float2 spectral = sourceNormal * Texel.zw * Optics.y * 2.2 * (ratio * ratio);
    float4 redSample = sampleClamped(Backdrop, sampleUv - spectral);
    float4 blueSample = sampleClamped(Backdrop, sampleUv + spectral);
    float dispMix = saturate(Optics.y * .55 * ratio * ratio);
    sharp.r = lerp(sharp.r, min(redSample.r, sharp.a), dispMix);
    sharp.b = lerp(sharp.b, min(blueSample.b, sharp.a), dispMix);

    // Blend cleanly: frosted calm interior -> crisp curved lens refraction at perimeter
    float sharpMix = pow(saturate((ratio - .16) / .84), 1.2) * Material.y;
    float4 color = lerp(frosted, sharp, sharpMix);

    // 3. Safely recover background color even when user lowers background opacity (premultiplied alpha)
    float srcAlpha = color.a;
    float3 sampleRgb = srcAlpha > .001 ? saturate(color.rgb / srcAlpha) : Tint.rgb;
    float3 rgb = lerp(Tint.rgb, sampleRgb, srcAlpha);
    float isDark = step(.5, Material.w);

    float luma = dot(rgb, float3(.299, .587, .114));
    rgb = saturate(luma + (rgb - luma) * lerp(1.16, 1.20, isDark));
    rgb = saturate(rgb * lerp(1.02, 1.04, isDark));

    // Adaptive luminance floor keeps light glass luminous over black desktops (without 65% paint)
    // and cushions harsh white glares under dark obsidian glass.
    float3 adapted = isDark > .5
        ? lerp(rgb, min(rgb, .72), .28 * saturate(Tint.a * 2.5))
        : lerp(rgb, max(rgb, .24), .24 * saturate(Tint.a * 2.5));
    rgb = lerp(adapted, Tint.rgb, saturate(Tint.a));

    // 4. Directional skylight diffuse scatter & lower-right subsurface caustic bounce
    float diag = pow(saturate(1.0 - (uv.x * .65 + uv.y * .55)), 1.9) * Material.x;
    float2 ca = float2((uv.x - .72) / .46, (uv.y - .86) / .34);
    float caustic = exp(-dot(ca, ca)) * (Material.x * .45);
    float3 darkGlow = saturate(Tint.rgb * 1.45 + .22);
    float3 lightDiff = saturate(rgb + (1.0 - rgb) * diag + caustic * .52);
    float3 darkDiff = saturate(lerp(rgb, darkGlow, diag) + darkGlow * (caustic * .48));
    rgb = lerp(lightDiff, darkDiff, isDark);

    // 5. Restrained diagonal corner catchlights (Screen blend) & thickness meniscus
    float light = dot(normal, normalize(Optics.zw));
    float diagWeight = .28 + .72 * sqrt(saturate(2.0 * abs(normal.x * normal.y)));
    float grazing = exp(-abs(distance + 1.2) * 1.15) * (1.0 - .55 * abs(light));
    rgb *= 1.0 - grazing * lerp(.09, .14, isDark);

    float rimBand = exp(-abs(distance + .65) * 1.45);
    float topArc = pow(saturate(light), 3.2) * lerp(.34, .26, isDark) * rimBand * diagWeight;
    float botArc = pow(saturate(-light), 3.8) * lerp(.18, .14, isDark) * rimBand * diagWeight;
    float fresnel = (ratio * ratio * ratio * ratio) * lerp(.055, .040, isDark);
    float spec = saturate((topArc + botArc + fresnel) * Optics.x);
    rgb = saturate(rgb + (1.0 - rgb) * spec);

    float outAlpha = max(srcAlpha, saturate(Tint.a + diag * .55 + caustic * .40 + spec * .85));
    float coverage = saturate(.5 - distance / max(Size.w, .25));
    return float4(rgb * outAlpha, outAlpha) * coverage;
}
