using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public class DXRRendererFeature : ScriptableRendererFeature
{
    public class DXRTracePass : ScriptableRenderPass
    {
        [System.Serializable]
        public class DXRSettings
        {
            public Color skyColor = Color.blue;
            public Color groundColor = Color.gray;
            public RayTracingShader rayTracingShader;
            public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        private DXRSettings settings;
        private RayTracingAccelerationStructure _rtas;
        private Material _accumulationMaterial;
        private int frameIndex;
        private bool pingPong;
        private RTHandle accumulationTarget1;
        private RTHandle accumulationTarget2;
        private Matrix4x4 lastCameraWorldMatrix;
        class TracePassData
        {
            public RayTracingShader RayTracingShader;
            public RayTracingAccelerationStructure RTAS;
            public uint width;
            public uint height;
            public Camera Camera;
            public TextureHandle DXRTarget;
            public Vector3 bottomLeft;
            public Vector3 topLeft;
            public Vector3 bottomRight;
            public Vector3 topRight;
            public int frameIndex;
            public Vector4 skyColor;
            public Vector4 groundColor;
        }

        class AccumulatePassData
        {
            public Material accumulationMaterial;
            public TextureHandle sourceTex;
            public int frameIndex;
            public bool pingPong;
            public TextureHandle accumulationTarget1;
            public TextureHandle accumulationTarget2;
        }

        public DXRTracePass(DXRSettings settings)
        {
            this.settings = settings;
            settings.rayTracingShader.SetShaderPass("DxrPass");
            _accumulationMaterial = new Material(Shader.Find("Hidden/Accumulation"));
            InitRTAS();
            settings.rayTracingShader.SetAccelerationStructure("_RaytracingAccelerationStructure",_rtas);
        }
        private void InitRTAS()
        {
            if (!SystemInfo.supportsRayTracing)
                return;

            RayTracingAccelerationStructure.Settings rtasSettings = new RayTracingAccelerationStructure.Settings
            {
                managementMode = RayTracingAccelerationStructure.ManagementMode.Automatic,
                rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
                layerMask = 1 << LayerMask.NameToLayer("Default")
            };

            _rtas = new RayTracingAccelerationStructure(rtasSettings);

            Renderer[] renderers = Object.FindObjectsOfType<Renderer>();
            RayTracingSubMeshFlags[ ] subMeshFlags = { RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly };
            foreach(Renderer r in renderers)
               _rtas.AddInstance(r, subMeshFlags, false);
            _rtas.Build();
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            var source = resourceData.activeColorTexture;
            var destinationDesc = renderGraph.GetTextureDesc(source);
            destinationDesc.enableRandomWrite = true;
            destinationDesc.name = "DXRTarget";
            TextureHandle buffer = renderGraph.CreateTexture(destinationDesc);

            var camera = cameraData.camera;

            int width = camera.pixelWidth;
            int height = camera.pixelHeight;
            Vector3 bottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, camera.farClipPlane)).normalized;
            Vector3 topLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, camera.farClipPlane)).normalized;
            Vector3 bottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, camera.farClipPlane)).normalized;
            Vector3 topRight = camera.ViewportToWorldPoint(new Vector3(1, 1, camera.farClipPlane)).normalized;

            if (lastCameraWorldMatrix != camera.transform.localToWorldMatrix)
            {
                // Reset accumulation frame counter
                frameIndex = 0;
                lastCameraWorldMatrix = camera.transform.localToWorldMatrix;
            }

            using (var builder = renderGraph.AddComputePass<TracePassData>(passName, out var passData))
            {
                passData.RayTracingShader = settings.rayTracingShader;
                passData.Camera = cameraData.camera;
                passData.DXRTarget = buffer;
                passData.width = (uint)width;
                passData.height = (uint)height;

                passData.bottomLeft = bottomLeft;
                passData.topLeft = topLeft;
                passData.bottomRight = bottomRight;
                passData.topRight = topRight;
                passData.frameIndex = frameIndex++;

                passData.skyColor = settings.skyColor.linear;
                passData.groundColor = settings.groundColor.linear;

                builder.UseTexture(passData.DXRTarget);

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((TracePassData data, ComputeGraphContext cgContext)
                    => ExecuteTracePass(data, cgContext));
            }

            RenderTextureDescriptor textureProperties = new RenderTextureDescriptor(width, height,
                RenderTextureFormat.Default, 0);

            RenderingUtils.ReAllocateIfNeeded(ref accumulationTarget1, textureProperties, FilterMode.Bilinear,
                TextureWrapMode.Clamp, name: "accumulation1" );
            TextureHandle accumulationTex1 = renderGraph.ImportTexture(accumulationTarget1);

            RenderingUtils.ReAllocateIfNeeded(ref accumulationTarget2, textureProperties, FilterMode.Bilinear,
                TextureWrapMode.Clamp, name: "accumulation2" );
            TextureHandle accumulationTex2 = renderGraph.ImportTexture(accumulationTarget2);

            using (var builder = renderGraph.AddRasterRenderPass("DXR accumulate", out AccumulatePassData passData))
            {
                passData.accumulationMaterial = _accumulationMaterial;
                passData.pingPong = pingPong;
                passData.sourceTex = buffer;
                passData.accumulationTarget1 = accumulationTex1;
                passData.accumulationTarget2 = accumulationTex2;
                passData.frameIndex = frameIndex;

                builder.AllowPassCulling(false);
                builder.UseTexture(pingPong?accumulationTex1:accumulationTex2);
                builder.UseTexture(buffer);
                builder.SetRenderAttachment(pingPong?accumulationTex2:accumulationTex1,0);

                builder.SetRenderFunc((AccumulatePassData data, RasterGraphContext rgContext) =>
                {
                    ExecuteAccumulatePass(data, rgContext);
                });
            }
            if (_accumulationMaterial == null)
            {
                _accumulationMaterial = new Material(Shader.Find("Hidden/Accumulation"));
            }
            resourceData.cameraColor = pingPong?accumulationTex2:accumulationTex1;
            pingPong = !pingPong;
        }

        static void ExecuteTracePass(TracePassData data, ComputeGraphContext context)
        {
            context.cmd.SetRayTracingVectorParam(data.RayTracingShader,"_TopLeftFrustumDir",data.topLeft);
            context.cmd.SetRayTracingVectorParam(data.RayTracingShader,"_TopRightFrustumDir",data.topRight);
            context.cmd.SetRayTracingVectorParam(data.RayTracingShader,"_BottomLeftFrustumDir",data.bottomLeft);
            context.cmd.SetRayTracingVectorParam(data.RayTracingShader,"_BottomRightFrustumDir",data.bottomRight);
            context.cmd.SetRayTracingVectorParam(data.RayTracingShader,"_CameraPos",data.Camera.transform.position);
            context.cmd.SetRayTracingVectorParam(data.RayTracingShader,"_SkyColor",data.skyColor);
            context.cmd.SetRayTracingVectorParam(data.RayTracingShader,"_GroundColor",data.groundColor);
            context.cmd.SetRayTracingTextureParam(data.RayTracingShader,"_DxrTarget", data.DXRTarget);
            context.cmd.SetRayTracingIntParam(data.RayTracingShader,"_FrameIndex", data.frameIndex);
            context.cmd.DispatchRays(data.RayTracingShader,"MyRaygenShader",data.width,data.height,1,data.Camera);
        }

        static void ExecuteAccumulatePass(AccumulatePassData data, RasterGraphContext context)
        {
            data.accumulationMaterial.SetTexture("_CurrentFrame", data.sourceTex);
            data.accumulationMaterial.SetTexture("_Accumulation", data.pingPong?data.accumulationTarget1:data.accumulationTarget2);
            data.accumulationMaterial.SetInt("_FrameIndex", data.frameIndex);
            Blitter.BlitTexture(context.cmd,data.sourceTex,new Vector4(1,1),data.accumulationMaterial,0);
        }
    }
    DXRTracePass customPass;
    public DXRTracePass.DXRSettings settings = new DXRTracePass.DXRSettings();

    public override void Create()
    {
        if(settings.rayTracingShader == null) return;
        customPass = new DXRTracePass(settings);
        customPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if(settings.rayTracingShader == null) return;
        renderer.EnqueuePass(customPass);
    }
}
