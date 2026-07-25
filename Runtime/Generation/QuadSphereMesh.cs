using UnityEngine;

namespace Valtiel.PlanetGenerator.Generation
{
    public static class QuadSphereMesh
    {
        // Face order matches Unity's CubemapFace enum: +X, -X, +Y, -Y, +Z, -Z.
        // For each face: forward = outward normal, right/up span the face in [-1, 1].
        static readonly Vector3[] FaceForward =
        {
            new( 1, 0, 0), new(-1, 0, 0),
            new( 0, 1, 0), new( 0,-1, 0),
            new( 0, 0, 1), new( 0, 0,-1),
        };
        static readonly Vector3[] FaceRight =
        {
            new(0, 0,-1), new(0, 0, 1),
            new(1, 0, 0), new(1, 0, 0),
            new(1, 0, 0), new(-1,0, 0),
        };
        static readonly Vector3[] FaceUp =
        {
            new(0, 1, 0), new(0, 1, 0),
            new(0, 0,-1), new(0, 0, 1),
            new(0, 1, 0), new(0, 1, 0),
        };

        public static Mesh Build(int subdivisions, float radius = 0.5f)
        {
            subdivisions = Mathf.Max(1, subdivisions);
            int vertsPerSide = subdivisions + 1;
            int vertsPerFace = vertsPerSide * vertsPerSide;
            int totalVerts = vertsPerFace * 6;
            int trisPerFace = subdivisions * subdivisions * 2;
            int totalIndices = trisPerFace * 3 * 6;

            var positions = new Vector3[totalVerts];
            var normals = new Vector3[totalVerts];
            var uvs = new Vector2[totalVerts];
            var indices = new int[totalIndices];

            int vi = 0, ii = 0;
            for (int f = 0; f < 6; f++)
            {
                var fwd = FaceForward[f];
                var right = FaceRight[f];
                var up = FaceUp[f];
                int faceBase = vi;

                for (int y = 0; y < vertsPerSide; y++)
                {
                    float v = (float)y / subdivisions;
                    float vv = v * 2f - 1f;
                    for (int x = 0; x < vertsPerSide; x++)
                    {
                        float u = (float)x / subdivisions;
                        float uu = u * 2f - 1f;

                        Vector3 cubePos = fwd + right * uu + up * vv;
                        Vector3 spherePos = cubePos.normalized;

                        positions[vi] = spherePos * radius;
                        normals[vi] = spherePos;
                        uvs[vi] = new Vector2(u, v);
                        vi++;
                    }
                }

                for (int y = 0; y < subdivisions; y++)
                {
                    for (int x = 0; x < subdivisions; x++)
                    {
                        int i0 = faceBase + y * vertsPerSide + x;
                        int i1 = i0 + 1;
                        int i2 = i0 + vertsPerSide;
                        int i3 = i2 + 1;

                        indices[ii++] = i0;
                        indices[ii++] = i1;
                        indices[ii++] = i2;
                        indices[ii++] = i1;
                        indices[ii++] = i3;
                        indices[ii++] = i2;
                    }
                }
            }

            var mesh = new Mesh { name = $"QuadSphere_{subdivisions}" };
            mesh.indexFormat = totalVerts > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = positions;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        public static readonly int[] DefaultLodSubdivisions = { 64, 32, 16, 8 };
    }
}
