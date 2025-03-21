using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using System.Collections.Generic;

public class DXRRendererFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class DXRSettings
    {
        public Color skyColor = Color.blue;
        public Color groundColor = Color.gray;
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public RayTracingShader rayTracingShader;
    }

    public DXRSettings settings = new DXRSettings();

    class DXRRenderPass : ScriptableRenderPass
    {
        private string profilerTag;
        private DXRSettings settings;
        private RenderTargetIdentifier cameraColorTarget;
        private RenderTargetHandle dxrTargetHandle;
        private RenderTargetHandle accumulationTarget1Handle;
        private RenderTargetHandle accumulationTarget2Handle;
        private RenderTexture dxrTarget;
        private RenderTexture accumulationTarget1;
        private RenderTexture accumulationTarget2;
        private Material accumulationMaterial;
        private RayTracingAccelerationStructure rtas;
        private RayTracingShader rayTracingShader;
        private int frameIndex;
        private Matrix4x4 lastCameraWorldMatrix;
        private bool rtasInitialized = false;
        private bool texturesInitialized = false;

        public DXRRenderPass(string tag, DXRSettings settings)
        {
            this.profilerTag = tag;
            this.settings = settings;
            this.renderPassEvent = settings.renderPassEvent;
            
            dxrTargetHandle.Init("_DXRTarget");
            accumulationTarget1Handle.Init("_AccumulationTarget1");
            accumulationTarget2Handle.Init("_AccumulationTarget2");
            
            frameIndex = 0;
            
            accumulationMaterial = new Material(Shader.Find("Hidden/Accumulation"));
            
            rayTracingShader = settings.rayTracingShader;
            if (rayTracingShader == null)
            {
                Debug.LogError("RayTracingShader not assigned in DXR Renderer Feature settings");
            }
        }

        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            RenderTextureDescriptor dxrDesc = cameraTextureDescriptor;
            dxrDesc.colorFormat = RenderTextureFormat.ARGBFloat;
            dxrDesc.depthBufferBits = 0;
            dxrDesc.msaaSamples = 1;
            dxrDesc.enableRandomWrite = true;
            
            if (dxrTarget == null || dxrTarget.width != cameraTextureDescriptor.width || dxrTarget.height != cameraTextureDescriptor.height)
            {
                if (dxrTarget != null)
                {
                    dxrTarget.Release();
                    accumulationTarget1.Release();
                    accumulationTarget2.Release();
                }
                
                dxrTarget = new RenderTexture(dxrDesc);
                dxrTarget.Create();
                
                accumulationTarget1 = new RenderTexture(dxrDesc);
                accumulationTarget1.Create();
                
                accumulationTarget2 = new RenderTexture(dxrDesc);
                accumulationTarget2.Create();
                
                cmd.SetGlobalTexture(dxrTargetHandle.id, dxrTarget);
                cmd.SetGlobalTexture(accumulationTarget1Handle.id, accumulationTarget1);
                cmd.SetGlobalTexture(accumulationTarget2Handle.id, accumulationTarget2);
                
                texturesInitialized = true;
                
                if (rtasInitialized && rayTracingShader != null)
                {
                    rayTracingShader.SetTexture("_DxrTarget", dxrTarget);
                }
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            cameraColorTarget = renderingData.cameraData.renderer.cameraColorTarget;
            
            if (!rtasInitialized && texturesInitialized)
            {
                InitRaytracingAccelerationStructure();
                rtasInitialized = true;
            }
            
            if (rtasInitialized)
            {
                Camera camera = renderingData.cameraData.camera;
                if (lastCameraWorldMatrix != camera.transform.localToWorldMatrix)
                {
                    UpdateParameters(cmd, camera);
                    lastCameraWorldMatrix = camera.transform.localToWorldMatrix;
                }
            }
        }

        private void InitRaytracingAccelerationStructure()
        {
            if (!SystemInfo.supportsRayTracing)
            {
                Debug.LogError("This device does not support raytracing");
                return;
            }

            RayTracingAccelerationStructure.RASSettings settings = new RayTracingAccelerationStructure.RASSettings();
            // Include all layers
            settings.layerMask = ~0;
            // Enable automatic updates
            settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Automatic;
            // Include all renderer types
            settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything;

            rtas = new RayTracingAccelerationStructure(settings);
            
            // Collect all objects in scene and add them to raytracing scene
            Renderer[] renderers = Object.FindObjectsOfType<Renderer>();
            foreach(Renderer r in renderers)
                rtas.AddInstance(r);

            // Build raytracing scene
            rtas.Build();
            
            rayTracingShader.SetAccelerationStructure("_RaytracingAccelerationStructure", rtas);
            // Now it's safe to set the texture since we know it's initialized
            rayTracingShader.SetTexture("_DxrTarget", dxrTarget);
            rayTracingShader.SetShaderPass("DxrPass");
        }

        private void UpdateParameters(CommandBuffer cmd, Camera camera)
        {
            // Update raytracing scene, in case something moved
            rtas.Build();

            // Frustum corners for current camera transform
            Vector3 bottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, camera.farClipPlane)).normalized;
            Vector3 topLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, camera.farClipPlane)).normalized;
            Vector3 bottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, camera.farClipPlane)).normalized;
            Vector3 topRight = camera.ViewportToWorldPoint(new Vector3(1, 1, camera.farClipPlane)).normalized;

            // Update camera, environment parameters
            rayTracingShader.SetVector("_SkyColor", settings.skyColor.gamma);
            rayTracingShader.SetVector("_GroundColor", settings.groundColor.gamma);

            rayTracingShader.SetVector("_TopLeftFrustumDir", topLeft);
            rayTracingShader.SetVector("_TopRightFrustumDir", topRight);
            rayTracingShader.SetVector("_BottomLeftFrustumDir", bottomLeft);
            rayTracingShader.SetVector("_BottomRightFrustumDir", bottomRight);

            rayTracingShader.SetVector("_CameraPos", camera.transform.position);

            // Reset accumulation frame counter
            frameIndex = 0;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!SystemInfo.supportsRayTracing || rtas == null || rayTracingShader == null || !texturesInitialized)
                return;

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            
            // Update frame index and start path tracer
            rayTracingShader.SetInt("_FrameIndex", frameIndex);
            
            Camera camera = renderingData.cameraData.camera;
            rayTracingShader.Dispatch("MyRaygenShader", camera.pixelWidth, camera.pixelHeight, 1, camera);
            
            cmd.SetGlobalTexture("_CurrentFrame", dxrTarget);
            cmd.SetGlobalTexture("_Accumulation", accumulationTarget1);
            cmd.SetGlobalInt("_FrameIndex", frameIndex++);
            
            // Accumulate current raytracing result
            cmd.Blit(dxrTarget, accumulationTarget2, accumulationMaterial);
            
            // Blit the accumulated result to the camera target
            cmd.Blit(accumulationTarget2, cameraColorTarget);
            
            // Switch accumulation textures for next frame
            RenderTexture temp = accumulationTarget1;
            accumulationTarget1 = accumulationTarget2;
            accumulationTarget2 = temp;
            
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void FrameCleanup(CommandBuffer cmd)
        {
            
        }
        
        public void Cleanup()
        {
            if (rtas != null)
                rtas.Release();
                
            if (dxrTarget != null)
                dxrTarget.Release();
                
            if (accumulationTarget1 != null)
                accumulationTarget1.Release();
                
            if (accumulationTarget2 != null)
                accumulationTarget2.Release();
        }
    }

    private DXRRenderPass dxrRenderPass;

    public override void Create()
    {
        dxrRenderPass = new DXRRenderPass("DXR Render Pass", settings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (SystemInfo.supportsRayTracing && settings.rayTracingShader != null)
        {
            renderer.EnqueuePass(dxrRenderPass);
        }
        else
        {
            Debug.LogWarning("DXR Renderer Feature: Raytracing not supported on this device or RayTracingShader not assigned.");
        }
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dxrRenderPass.Cleanup();
        }
        base.Dispose(disposing);
    }
}