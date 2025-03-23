using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

[RequireComponent(typeof(Camera))]
public class DXRCamera : MonoBehaviour
{
    private Material _accumulationMaterial;
    private RayTracingAccelerationStructure rtas;
    [SerializeField]private RayTracingShader rayTracingShader;
    private int frameIndex;
    public Color skyColor = Color.blue;
    public Color groundColor = Color.gray;
    private bool rtasInitialized = false;

    [SerializeField]private RenderTexture dxrTarget;
    private RenderTexture _accumulationTarget1;
    [SerializeField]public RenderTexture _accumulationTarget2;
    private Matrix4x4 _cameraWorldMatrix;

    private Camera camera;

    private void Start()
    {
        InitDXR();
    }

    private void Update()
    {
        UpdateDXR();
    }

    public void InitDXR()
    {
        camera = GetComponent<Camera>();
        rayTracingShader = Resources.Load<RayTracingShader>("RayTracingShader");

        CreateDestinationTexture();
        rayTracingShader.SetTexture("_DxrTarget", dxrTarget);
        rayTracingShader.SetShaderPass("DxrPass");
        if (!rtasInitialized)
        {
            InitRTAS();
            rtasInitialized = true;
        }
        rayTracingShader.SetAccelerationStructure("_RaytracingAccelerationStructure", rtas);
        _accumulationMaterial = new Material(Shader.Find("Hidden/Accumulation"));
    }

    public void UpdateDXR()
    {
        if(rayTracingShader == null || !SystemInfo.supportsRayTracing) return;
        UpdateParameters(camera);
        rayTracingShader.SetVector("_SkyColor", skyColor.linear);
        rayTracingShader.SetVector("_GroundColor", groundColor.linear);
        rayTracingShader.SetInt("_FrameIndex", frameIndex);
        rtas.Build();
        RenderDXR();
    }

    private void RenderDXR()
    {
        // update frame index and start path tracer
        rayTracingShader.SetInt("_FrameIndex", frameIndex);
        // start one thread for each pixel on screen
        rayTracingShader.Dispatch("MyRaygenShader", camera.pixelWidth, camera.pixelHeight, 1, camera);

        // update accumulation material
        _accumulationMaterial.SetTexture("_CurrentFrame", this.dxrTarget);
        _accumulationMaterial.SetTexture("_Accumulation", _accumulationTarget1);
        _accumulationMaterial.SetInt("_FrameIndex", frameIndex++);

        // accumulate current raytracing result
        Graphics.Blit(dxrTarget, _accumulationTarget2, _accumulationMaterial);
        // display result on screen
        //Graphics.Blit(_accumulationTarget2, destination);

        // switch accumulate textures
        var temp = _accumulationTarget1;
        _accumulationTarget1 = _accumulationTarget2;
        _accumulationTarget2 = temp;
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


        rtas = new RayTracingAccelerationStructure(rtasSettings);

        Renderer[] renderers = Object.FindObjectsOfType<Renderer>();
        RayTracingSubMeshFlags[ ] subMeshFlags = { RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly };
        foreach(Renderer r in renderers)
            Debug.Log(rtas.AddInstance(r, subMeshFlags, false));
        rtas.Build();
    }

    [ContextMenu("add instances")]
    private void AddInstances()
    {
        rtas.ClearInstances();
        Renderer[] renderers = Object.FindObjectsOfType<Renderer>();
        RayTracingSubMeshFlags[ ] subMeshFlags = { RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly | RayTracingSubMeshFlags.UniqueAnyHitCalls};
        foreach(Renderer r in renderers)
            rtas.AddInstance(r, subMeshFlags);
    }

    private void UpdateParameters(Camera camera)
    {
        rtas.Build();

        Vector3 bottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, camera.farClipPlane)).normalized;
        Vector3 topLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, camera.farClipPlane)).normalized;
        Vector3 bottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, camera.farClipPlane)).normalized;
        Vector3 topRight = camera.ViewportToWorldPoint(new Vector3(1, 1, camera.farClipPlane)).normalized;

        rayTracingShader.SetVector("_TopLeftFrustumDir", topLeft);
        rayTracingShader.SetVector("_TopRightFrustumDir", topRight);
        rayTracingShader.SetVector("_BottomLeftFrustumDir", bottomLeft);
        rayTracingShader.SetVector("_BottomRightFrustumDir", bottomRight);
        rayTracingShader.SetVector("_CameraPos", camera.transform.position);
    }

    private void CreateDestinationTexture()
    {
        int width = camera.pixelWidth;
        int height = camera.pixelHeight;

        if (dxrTarget != null && dxrTarget.width == width && dxrTarget.height == height)
            return;

        if (dxrTarget != null)
            dxrTarget.Release();

        dxrTarget = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true
        };
        dxrTarget.Create();
        _accumulationTarget1 = new RenderTexture(dxrTarget);
        _accumulationTarget2 = new RenderTexture(dxrTarget);
    }

    private void UpdateRenderTextures()
    {

    }
}
