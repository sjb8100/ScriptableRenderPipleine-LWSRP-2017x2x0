using System;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.PostProcessing
{
    [Serializable]
    public sealed class TemporalAntialiasing
    {
        [Tooltip("The diameter (in texels) inside which jitter samples are spread. Smaller values result in crisper but more aliased output, while larger values result in more stable but blurrier output.")]
        [Range(0.1f, 1f)]
        public float jitterSpread = 0.75f;

        [Tooltip("Controls the amount of sharpening applied to the color buffer.")]
        [Range(0f, 3f)]
        public float sharpen = 0.25f;

        [Tooltip("The blend coefficient for a stationary fragment. Controls the percentage of history sample blended into the final color.")]
        [Range(0f, 0.99f)]
        public float stationaryBlending = 0.95f;

        [Tooltip("The blend coefficient for a fragment with significant motion. Controls the percentage of history sample blended into the final color.")]
        [Range(0f, 0.99f)]
        public float motionBlending = 0.85f;

        // For custom jittered matrices - use at your own risks
        public Func<Camera, Vector2, Matrix4x4> jitteredMatrixFunc;
        
        Vector2 m_Jitter;
        public Vector2 jitter { get { return m_Jitter; } }

        enum Pass
        {
            SolverDilate,
            SolverNoDilate,
            AlphaClear
        }

        const string k_ShaderString = "Hidden/PostProcessing/TemporalAntialiasing";
        const int k_SampleCount = 8;
        int m_SampleIndex;
        readonly RenderTargetIdentifier[] m_Mrt = new RenderTargetIdentifier[2];
        bool m_ResetHistory = true;

        // Ping-pong between two history textures as we can't read & write the same target in the
        // same pass
        readonly RenderTexture[] m_HistoryTextures = new RenderTexture[2];
        int m_HistoryPingPong;

        public bool IsSupported()
        {
            return SystemInfo.supportedRenderTargetCount >= 2
                && SystemInfo.supportsMotionVectors;
        }

        internal DepthTextureMode GetLegacyCameraFlags()
        {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        internal void ResetHistory()
        {
            m_ResetHistory = true;
        }

        float GetHaltonValue(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / (float)radix;

            while (index > 0)
            {
                result += (float)(index % radix) * fraction;

                index /= radix;
                fraction /= (float)radix;
            }

            return result;
        }

        Vector2 GenerateRandomOffset()
        {
            var offset = new Vector2(
                    GetHaltonValue(m_SampleIndex & 1023, 2),
                    GetHaltonValue(m_SampleIndex & 1023, 3)
                );

            if (++m_SampleIndex >= k_SampleCount)
                m_SampleIndex = 0;

            return offset;
        }

        // Adapted heavily from PlayDead's TAA code
        // https://github.com/playdeadgames/temporal/blob/master/Assets/Scripts/Extensions.cs
        Matrix4x4 GetPerspectiveProjectionMatrix(Camera camera, Vector2 offset)
        {
            float vertical = Mathf.Tan(0.5f * Mathf.Deg2Rad * camera.fieldOfView);
            float horizontal = vertical * camera.aspect;
            float near = camera.nearClipPlane;
            float far = camera.farClipPlane;

            offset.x *= horizontal / (0.5f * camera.pixelWidth);
            offset.y *= vertical / (0.5f * camera.pixelHeight);

            float left = (offset.x - horizontal) * near;
            float right = (offset.x + horizontal) * near;
            float top = (offset.y + vertical) * near;
            float bottom = (offset.y - vertical) * near;

            var matrix = new Matrix4x4();

            matrix[0, 0] = (2f * near) / (right - left);
            matrix[0, 1] = 0f;
            matrix[0, 2] = (right + left) / (right - left);
            matrix[0, 3] = 0f;

            matrix[1, 0] = 0f;
            matrix[1, 1] = (2f * near) / (top - bottom);
            matrix[1, 2] = (top + bottom) / (top - bottom);
            matrix[1, 3] = 0f;

            matrix[2, 0] = 0f;
            matrix[2, 1] = 0f;
            matrix[2, 2] = -(far + near) / (far - near);
            matrix[2, 3] = -(2f * far * near) / (far - near);

            matrix[3, 0] = 0f;
            matrix[3, 1] = 0f;
            matrix[3, 2] = -1f;
            matrix[3, 3] = 0f;

            return matrix;
        }

        Matrix4x4 GetOrthographicProjectionMatrix(Camera camera, Vector2 offset)
        {
            float vertical = camera.orthographicSize;
            float horizontal = vertical * camera.aspect;

            offset.x *= horizontal / (0.5f * camera.pixelWidth);
            offset.y *= vertical / (0.5f * camera.pixelHeight);

            float left = offset.x - horizontal;
            float right = offset.x + horizontal;
            float top = offset.y + vertical;
            float bottom = offset.y - vertical;

            return Matrix4x4.Ortho(left, right, bottom, top, camera.nearClipPlane, camera.farClipPlane);
        }

        public void SetProjectionMatrix(Camera camera)
        {
            m_Jitter = GenerateRandomOffset();
            m_Jitter *= jitterSpread;

            camera.nonJitteredProjectionMatrix = camera.projectionMatrix;

            if (jitteredMatrixFunc != null)
            {
                camera.projectionMatrix = jitteredMatrixFunc(camera, m_Jitter);
            }
            else
            {
                camera.projectionMatrix = camera.orthographic
                    ? GetOrthographicProjectionMatrix(camera, m_Jitter)
                    : GetPerspectiveProjectionMatrix(camera, m_Jitter);
            }

        #if UNITY_5_5_OR_NEWER
            camera.useJitteredProjectionMatrixForTransparentRendering = false;
        #endif

            m_Jitter.x /= camera.pixelWidth;
            m_Jitter.y /= camera.pixelHeight;
        }

        RenderTexture CheckHistory(int id, PostProcessRenderContext context, PropertySheet sheet)
        {
            var rt = m_HistoryTextures[id];

            if (m_ResetHistory || rt == null || !rt.IsCreated() || rt.width != context.width || rt.height != context.height)
            {
                RenderTexture.ReleaseTemporary(rt);

                rt = RenderTexture.GetTemporary(context.width, context.height, 0, context.sourceFormat);
                rt.name = "Temporal Anti-aliasing History";
                rt.filterMode = FilterMode.Bilinear;
                m_HistoryTextures[id] = rt;

                context.command.BlitFullscreenTriangle(context.source, rt, sheet, (int)Pass.AlphaClear);
            }

            return rt;
        }

        internal void Render(PostProcessRenderContext context)
        {
            var sheet = context.propertySheets.Get(k_ShaderString);

            var cmd = context.command;
            cmd.BeginSample("TemporalAntialiasing");

            int pp = m_HistoryPingPong;
            var historyRead = CheckHistory(++pp % 2, context, sheet);
            var historyWrite = CheckHistory(++pp % 2, context, sheet);
            m_HistoryPingPong = ++pp % 2;

            const float kMotionAmplification = 100f * 60f;
            sheet.properties.SetVector(Uniforms._Jitter, m_Jitter);
            sheet.properties.SetFloat(Uniforms._SharpenParameters, sharpen);
            sheet.properties.SetVector(Uniforms._FinalBlendParameters, new Vector4(stationaryBlending, motionBlending, kMotionAmplification, 0f));
            sheet.properties.SetTexture(Uniforms._HistoryTex, historyRead);

            int pass = context.camera.orthographic ? (int)Pass.SolverNoDilate : (int)Pass.SolverDilate;
            m_Mrt[0] = context.destination;
            m_Mrt[1] = historyWrite;

            cmd.BlitFullscreenTriangle(context.source, m_Mrt, context.source, sheet, pass);
            cmd.EndSample("TemporalAntialiasing");

            m_ResetHistory = false;
        }

        internal void Release()
        {
            for (int i = 0; i < m_HistoryTextures.Length; i++)
            {
                RenderTexture.ReleaseTemporary(m_HistoryTextures[i]);
                m_HistoryTextures[i] = null;
            }

            m_SampleIndex = 0;
            m_HistoryPingPong = 0;
            
            ResetHistory();
        }
    }
}
