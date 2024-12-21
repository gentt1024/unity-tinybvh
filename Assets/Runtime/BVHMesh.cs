using System.Collections.Generic;
using UnityEngine;

namespace tinybvh
{
    public class BVHMesh : BVHMeshCompound
    {
        public BVHMesh(Mesh mesh) : base(new List<(Mesh mesh, Matrix4x4 matrix)>{(mesh, Matrix4x4.identity)})
        {
        }
    }
}