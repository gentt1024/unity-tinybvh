using System;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

public class BVHManager : MonoBehaviour
{
    public MeshRenderer[] meshRenderers;

    private tinybvh.BVH sceneBVH;
    private bool buildingBVH = false;

    private ComputeShader meshProcessingShader;
    private LocalKeyword has32BitIndicesKeyword;
    private LocalKeyword hasNormalsKeyword;
    private LocalKeyword hasUVsKeyword;

    public ComputeBuffer vertexPositionBufferGPU;
    public NativeArray<Vector4> vertexPositionBufferCPU;
    private ComputeBuffer triangleAttributesBuffer;

    // CWBVH data from tinybvh
    private ComputeBuffer bvhNodes;
    private ComputeBuffer bvhTris;

    private int totalVertexCount = 0;
    private int totalTriangleCount = 0;
    private DateTime readbackStartTime;

    // Struct sizes in bytes
    private const int VertexPositionSize = 16;
    private const int TriangleAttributeSize = 60;
    private const int BVHNodeSize = 80;
    private const int BVHTriSize = 16;

    void Start()
    {
        sceneBVH = new tinybvh.BVH();

        // Load compute shader
        meshProcessingShader   = Resources.Load<ComputeShader>("MeshProcessing");
        has32BitIndicesKeyword = meshProcessingShader.keywordSpace.FindKeyword("HAS_32_BIT_INDICES");
        hasNormalsKeyword      = meshProcessingShader.keywordSpace.FindKeyword("HAS_NORMALS");
        hasUVsKeyword          = meshProcessingShader.keywordSpace.FindKeyword("HAS_UVS");

        // If an explicit list of renderers wasn't provided we auto-populate the list
        if (meshRenderers.Length == 0)
        {
            meshRenderers = FindObjectsOfType<MeshRenderer>();
        }

        ProcessMeshes();
    }

    void OnDestroy()
    {
        vertexPositionBufferGPU?.Release();
        triangleAttributesBuffer?.Release();

        if (vertexPositionBufferCPU.IsCreated)
        {
            vertexPositionBufferCPU.Dispose();
        }

        sceneBVH.Destroy();
    }

    public tinybvh.BVH GetBVH()
    {
        return sceneBVH;
    }

    private void ProcessMeshes()
    {
        totalVertexCount = 0;
        totalTriangleCount = 0;

        // Gather info on the meshes we'll be using
        foreach (MeshRenderer renderer in meshRenderers)
        {
            Mesh mesh = renderer.gameObject.GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null)
            {
                continue;
            }

            totalVertexCount += Utilities.GetTriangleCount(mesh) * 3;
        }

        // Allocate buffers
        vertexPositionBufferGPU  = new ComputeBuffer(totalVertexCount, VertexPositionSize);
        vertexPositionBufferCPU  = new NativeArray<Vector4>(totalVertexCount * VertexPositionSize, Allocator.Persistent);
        triangleAttributesBuffer = new ComputeBuffer(totalVertexCount / 3, TriangleAttributeSize);

        // Pack each mesh into global vertex buffer via compute shader
        // Note: this avoids the need for every mesh to have cpu read/write access.
        foreach (MeshRenderer renderer in meshRenderers)
        {
            Mesh mesh = renderer.gameObject.GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null)
            {
                continue;
            }

            mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            GraphicsBuffer vertexBuffer = mesh.GetVertexBuffer(0);

            mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
            GraphicsBuffer indexBuffer = mesh.GetIndexBuffer();
            
            int triangleCount = Utilities.GetTriangleCount(mesh);

            // Determine where in the Unity vertex buffer each vertex attribute is
            int vertexStride, positionOffset, normalOffset, uvOffset;
            Utilities.FindVertexAttribute(mesh, VertexAttribute.Position, out vertexStride, out positionOffset);
            Utilities.FindVertexAttribute(mesh, VertexAttribute.Normal, out vertexStride, out normalOffset);
            Utilities.FindVertexAttribute(mesh, VertexAttribute.TexCoord0, out vertexStride, out uvOffset);

            meshProcessingShader.SetBuffer(0, "VertexBuffer", vertexBuffer);
            meshProcessingShader.SetBuffer(0, "IndexBuffer", indexBuffer);
            meshProcessingShader.SetBuffer(0, "VertexPositionBuffer", vertexPositionBufferGPU);
            meshProcessingShader.SetBuffer(0, "TriangleAttributesBuffer", triangleAttributesBuffer);
            meshProcessingShader.SetInt("VertexStride", vertexStride);
            meshProcessingShader.SetInt("PositionOffset", positionOffset);
            meshProcessingShader.SetInt("NormalOffset", normalOffset);
            meshProcessingShader.SetInt("UVOffset", uvOffset);
            meshProcessingShader.SetInt("TriangleCount", triangleCount);
            meshProcessingShader.SetInt("OutputTriangleStart", totalTriangleCount);
            meshProcessingShader.SetMatrix("LocalToWorld", renderer.localToWorldMatrix);

            // Set keywords based on format/attributes of this mesh
            meshProcessingShader.SetKeyword(has32BitIndicesKeyword, (mesh.indexFormat == IndexFormat.UInt32));
            meshProcessingShader.SetKeyword(hasNormalsKeyword, mesh.HasVertexAttribute(VertexAttribute.Normal));
            meshProcessingShader.SetKeyword(hasUVsKeyword, mesh.HasVertexAttribute(VertexAttribute.TexCoord0));

            meshProcessingShader.Dispatch(0, Mathf.CeilToInt(triangleCount / 64.0f), 1, 1);

            totalTriangleCount += triangleCount;
        }

        Debug.Log("Meshes processed. Total triangles: " + totalTriangleCount);

        // Initiate async readback of vertex buffer to pass to tiny_bvh to build
        readbackStartTime = DateTime.UtcNow;
        AsyncGPUReadback.RequestIntoNativeArray(ref vertexPositionBufferCPU, vertexPositionBufferGPU, OnCompleteReadback);
    }

    private unsafe void OnCompleteReadback(AsyncGPUReadbackRequest request)
    {
        if (request.hasError)
        {
            Debug.LogError("GPU readback error.");
            return;
        }

        TimeSpan readbackTime = DateTime.UtcNow - readbackStartTime;
        Debug.Log("GPU readback took: " + readbackTime.TotalMilliseconds + "ms");

        var dataPointer = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(vertexPositionBufferCPU);
        
        // Build BVH in thread.
        Thread thread = new Thread(() =>
        {
            DateTime bvhStartTime = DateTime.UtcNow;
            sceneBVH.Build(dataPointer, totalTriangleCount, true);
            TimeSpan bvhTime = DateTime.UtcNow - bvhStartTime;

            Debug.Log("BVH built in: " + bvhTime.TotalMilliseconds + "ms");
        });

        buildingBVH = true;
        thread.Start();
    }

    private void Update()
    {
        if (buildingBVH && sceneBVH.IsReady())
        {
            // Get the sizes of the arrays
            int nodesSize = sceneBVH.GetCWBVHNodesSize();
            int trisSize = sceneBVH.GetCWBVHTrisSize();

            IntPtr nodesPtr, trisPtr;
            if (sceneBVH.GetCWBVHData(out nodesPtr, out trisPtr))
            {
                Utilities.UploadFromPointer(ref bvhNodes, nodesPtr, nodesSize, BVHNodeSize);
                Utilities.UploadFromPointer(ref bvhTris, trisPtr, trisSize, BVHTriSize);
            } 
            else
            {
                Debug.LogError("Failed to fetch updated BVH data.");
            }

            buildingBVH = false;
        }
    }

    public bool CanRender()
    {
        return (bvhNodes != null && bvhTris != null);
    }

    public void PrepareShader(CommandBuffer cmd, ComputeShader shader, int kernelIndex)
    {
        if (bvhNodes == null || bvhTris == null || triangleAttributesBuffer == null)
        {
            return;
        }

        cmd.SetComputeBufferParam(shader, kernelIndex, "BVHNodes", bvhNodes);
        cmd.SetComputeBufferParam(shader, kernelIndex, "BVHTris", bvhTris);
        cmd.SetComputeBufferParam(shader, kernelIndex, "TriangleAttributesBuffer", triangleAttributesBuffer);
    }
}
