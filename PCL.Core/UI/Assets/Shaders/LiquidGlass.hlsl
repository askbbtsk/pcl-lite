// LiquidGlass.hlsl —— PCL Lite 液态玻璃材质（覆盖层版）
//
// 为什么不再采样控件本身：
//   WPF 把带 Effect 的元素先渲染进一张中间位图再贴回来，控件只要落在非整数像素上，
//   这张位图就会被重采样，边缘那圈抗锯齿像素的透明度会被改写（0.52 → 0.74），
//   视觉上就是一圈洗不掉的毛边，连纯直通的着色器也躲不掉。
//   所以这里改成「不碰控件本体」：玻璃作为一层独立的覆盖层画在控件上方，
//   着色器完全不读输入纹理，颜色全部由距离场现算。
//
// 光照分层（从外到内）：
//   0. 最外 0.8px  —— 交给控件自己的抗锯齿，覆盖层不参与，避免二次描边
//   1. 边缘聚光    —— 整圈贴着边界的一层亮光，靠边最亮。
//                     真实玻璃的边缘因为全反射会比中间亮，这一层就是「折射感」的来源。
//   2. 顶部亮线    —— 上沿更强的一道高光，交代光从上面来
//   3. 色散        —— 靠外偏暖、靠内偏冷，模拟不同波长的折射率差异
//   4. 底部内阴影  —— 下沿压一条暗带，这是「玻璃是凸的」最关键的一笔

// 与 C# 端 PixelShaderConstantCallback 一一对应
float2 BorderSize : register(C0);      // 控件尺寸（像素）
float CornerRadius : register(C1);     // 圆角半径
float EffectWidth : register(C2);      // 影响带宽（像素）
float RefractionPower : register(C3);  // 边缘聚光强度 0-1（界面上的「折射强度」）
float Highlight : register(C4);        // 顶部高光强度 0-1（界面上的「高光亮度」）
float4 Tint : register(C5);            // 玻璃色调，a 为融合比例
float Dispersion : register(C6);       // 色散强度 0-1

// 2D 圆角矩形有向距离场：<0 内部，=0 边缘，>0 外部
float SdRoundRect(float2 p, float2 b, float r)
{
    float2 d = abs(p) - (b - r);
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - r;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 halfSize = BorderSize * 0.5;
    float2 p = (uv - 0.5) * BorderSize;
    float r = min(CornerRadius, min(halfSize.x, halfSize.y));
    float dist = SdRoundRect(p, halfSize, r);

    // 形状抗锯齿：边界内侧 0.8px 渐变，外侧直接透明
    float shapeA = 1.0 - smoothstep(-0.8, 0.0, dist);
    if (shapeA <= 0.002)
        return float4(0.0, 0.0, 0.0, 0.0);

    float depth = -dist;                               // 距边界向内的深度
    float yUp = saturate(-p.y / max(halfSize.y, 1.0)); // 0 = 底，1 = 顶
    float band = saturate(1.0 - depth / max(EffectWidth, 1.0)); // 1 = 贴边，0 = 内部

    // ---------- 1. 边缘聚光 ----------
    float glow = pow(band, 2.0);
    // 靠外偏冷（蓝），靠内转白，形成玻璃的层次
    float3 glowColor = lerp(float3(0.60, 0.79, 1.00), float3(1.00, 1.00, 1.00),
                            saturate(depth / max(EffectWidth * 0.75, 1.0)));
    // 色散：R 提亮、B 压暗，越靠边越明显
    glowColor *= float3(1.0 + 0.10 * Dispersion, 1.0, 1.0 - 0.10 * Dispersion);
    glowColor = lerp(glowColor, Tint.rgb, Tint.a);
    float aGlow = glow * RefractionPower * 0.85;

    // ---------- 2. 顶部亮线 ----------
    float topW = smoothstep(0.35, 0.95, yUp);
    float topMask = smoothstep(0.25, 0.95, depth) * (1.0 - smoothstep(1.40, 3.20, depth));
    float aTop = topMask * topW * Highlight * 0.90;

    // ---------- 3. 底部内阴影 ----------
    float botW = 1.0 - smoothstep(0.00, 0.40, yUp);
    float botMask = smoothstep(0.40, 1.20, depth) * (1.0 - smoothstep(2.00, 4.20, depth));
    float aBot = botMask * botW * Highlight * 0.35;
    float3 botColor = float3(0.02, 0.05, 0.12);

    // ---------- 合成 ----------
    float total = aGlow + aTop + aBot;
    float3 col = (aGlow * glowColor + aTop * float3(1.0, 1.0, 1.0) + aBot * botColor)
                 / max(total, 1e-5);
    float a = saturate(total);

    return float4(col * a * shapeA, a * shapeA);
}
