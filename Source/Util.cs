using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Carousel
{
    public static class Util
    {
        public static CarouselMapComp CarouselComp(this Map map)
        {
            return map.GetComponent<CarouselMapComp>();
        }

        public static CellRect Inside(this CellRect a, CellRect b)
        {
            if (a.maxX > b.maxX)
                a = a.MovedBy(new IntVec2(b.maxX - a.maxX, 0));
            if (a.maxZ > b.maxZ)
                a = a.MovedBy(new IntVec2(0, b.maxZ - a.maxZ));
            if (a.minX < b.minX)
                a = a.MovedBy(new IntVec2(b.minX - a.minX, 0));
            if (a.minZ < b.minZ)
                a = a.MovedBy(new IntVec2(0, b.minZ - a.minZ));

            return a;
        }

        public static V GetOrAdd<K, V>(this Dictionary<K, V> dict, K key, Func<K, V> def)
        {
            if (!dict.TryGetValue(key, out var val))
                return dict[key] = def(key);
            return val;
        }

        public static void SetUVs(this Mesh mesh, Vector3[] uvs, int len)
        {
            mesh.SetSizedArrayForChannel(
                VertexAttribute.TexCoord0,
                VertexAttributeFormat.Float32,
                3,
                uvs,
                len,
                0,
                len
            );
        }

        public static void SetVertices(this Mesh mesh, Vector3[] verts, int len)
        {
            mesh.SetSizedArrayForChannel(
                VertexAttribute.Position,
                VertexAttributeFormat.Float32,
                3,
                verts,
                len,
                0,
                len
            );
        }

        public static void ResizeIfNeeded<T>(ref T[] arr, int len)
        {
            if (arr.Length < len)
                arr = new T[Math.Max(len, arr.Length * 2)];
        }

        public static void SetXZY(ref this Vector3 vec, ref Vector3 from, float y)
        {
            vec.x = from.x;
            vec.z = from.z;
            vec.y = y;
        }
    }
}
