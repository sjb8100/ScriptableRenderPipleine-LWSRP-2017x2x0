using System;

namespace UnityEngine.Experimental.PostProcessing
{
    [Serializable]
    [PostProcess(typeof(MotionBlurRenderer), "Unity/Motion Blur", false)]
    public sealed class MotionBlur : PostProcessEffectSettings
    {
        [Range(0f, 360f), Tooltip("The angle of rotary shutter. Larger values give longer exposure.")]
        public FloatParameter shutterAngle = new FloatParameter { value = 270f };

        [Range(4, 32), Tooltip("The amount of sample points, which affects quality and performances.")]
        public IntParameter sampleCount = new IntParameter { value = 10 };

        public override bool IsEnabledAndSupported()
        {
            return enabled.value
                && shutterAngle.value > 0f
            #if UNITY_EDITOR
                // Don't render motion blur preview when the editor is not playing as it can in some
                // cases results in ugly artifacts (i.e. when resizing the game view).
                && Application.isPlaying
            #endif
                && SystemInfo.supportsMotionVectors
                && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf);
        }
    }
    
    public sealed class MotionBlurRenderer : PostProcessEffectRenderer<MotionBlur>
    {
        enum Pass
        {
            VelocitySetup,
            TileMax1,
            TileMax2,
            TileMaxV,
            NeighborMax,
            Reconstruction
        }

        public override DepthTextureMode GetLegacyCameraFlags()
        {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        public override void Render(PostProcessRenderContext context)
        {
            var cmd = context.command;

            if (m_ResetHistory)
            {
                cmd.BlitFullscreenTriangle(context.source, context.destination);
                m_ResetHistory = false;
                return;
            }

            const float kMaxBlurRadius = 5f;
            var vectorRTFormat = RenderTextureFormat.RGHalf;
            var packedRTFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB2101010)
                ? RenderTextureFormat.ARGB2101010
                : RenderTextureFormat.ARGB32;

            var sheet = context.propertySheets.Get("Hidden/PostProcessing/MotionBlur");
            cmd.BeginSample("MotionBlur");

            // Calculate the maximum blur radius in pixels.
            int maxBlurPixels = (int)(kMaxBlurRadius * context.height / 100);

            // Calculate the TileMax size.
            // It should be a multiple of 8 and larger than maxBlur.
            int tileSize = ((maxBlurPixels - 1) / 8 + 1) * 8;

            // Pass 1 - Velocity/depth packing
            var velocityScale = settings.shutterAngle / 360f;
            sheet.properties.SetFloat(Uniforms._VelocityScale, velocityScale);
            sheet.properties.SetFloat(Uniforms._MaxBlurRadius, maxBlurPixels);
            sheet.properties.SetFloat(Uniforms._RcpMaxBlurRadius, 1f / maxBlurPixels);

            int vbuffer = Uniforms._VelocityTex;
            cmd.GetTemporaryRT(vbuffer, context.width, context.height, 0, FilterMode.Point,
                packedRTFormat, RenderTextureReadWrite.Linear);
            cmd.BlitFullscreenTriangle((Texture)null, vbuffer, sheet, (int)Pass.VelocitySetup);

            // Pass 2 - First TileMax filter (1/2 downsize)
            int tile2 = Uniforms._Tile2RT;
            cmd.GetTemporaryRT(tile2, context.width / 2, context.height / 2, 0, FilterMode.Point,
                vectorRTFormat, RenderTextureReadWrite.Linear);
            cmd.BlitFullscreenTriangle(vbuffer, tile2, sheet, (int)Pass.TileMax1);

            // Pass 3 - Second TileMax filter (1/2 downsize)
            int tile4 = Uniforms._Tile4RT;
            cmd.GetTemporaryRT(tile4, context.width / 4, context.height / 4, 0, FilterMode.Point,
                vectorRTFormat, RenderTextureReadWrite.Linear);
            cmd.BlitFullscreenTriangle(tile2, tile4, sheet, (int)Pass.TileMax2);
            cmd.ReleaseTemporaryRT(tile2);

            // Pass 4 - Third TileMax filter (1/2 downsize)
            int tile8 = Uniforms._Tile8RT;
            cmd.GetTemporaryRT(tile8, context.width / 8, context.height / 8, 0, FilterMode.Point,
                vectorRTFormat, RenderTextureReadWrite.Linear);
            cmd.BlitFullscreenTriangle(tile4, tile8, sheet, (int)Pass.TileMax2);
            cmd.ReleaseTemporaryRT(tile4);

            // Pass 5 - Fourth TileMax filter (reduce to tileSize)
            var tileMaxOffs = Vector2.one * (tileSize / 8f - 1f) * -0.5f;
            sheet.properties.SetVector(Uniforms._TileMaxOffs, tileMaxOffs);
            sheet.properties.SetFloat(Uniforms._TileMaxLoop, (int)(tileSize / 8f));

            int tile = Uniforms._TileVRT;
            cmd.GetTemporaryRT(tile, context.width / tileSize, context.height / tileSize, 0,
                FilterMode.Point, vectorRTFormat, RenderTextureReadWrite.Linear);
            cmd.BlitFullscreenTriangle(tile8, tile, sheet, (int)Pass.TileMaxV);
            cmd.ReleaseTemporaryRT(tile8);

            // Pass 6 - NeighborMax filter
            int neighborMax = Uniforms._NeighborMaxTex;
            int neighborMaxWidth = context.width / tileSize;
            int neighborMaxHeight = context.height / tileSize;
            cmd.GetTemporaryRT(neighborMax, neighborMaxWidth, neighborMaxHeight, 0,
                FilterMode.Point, vectorRTFormat, RenderTextureReadWrite.Linear);
            cmd.BlitFullscreenTriangle(tile, neighborMax, sheet, (int)Pass.NeighborMax);
            cmd.ReleaseTemporaryRT(tile);

            // Pass 7 - Reconstruction pass
            sheet.properties.SetFloat(Uniforms._LoopCount, Mathf.Clamp(settings.sampleCount / 2, 1, 64));
            cmd.BlitFullscreenTriangle(context.source, context.destination, sheet, (int)Pass.Reconstruction);

            cmd.ReleaseTemporaryRT(vbuffer);
            cmd.ReleaseTemporaryRT(neighborMax);
            cmd.EndSample("MotionBlur");
        }
    }
}
