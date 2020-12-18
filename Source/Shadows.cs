using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Carousel
{
    [HarmonyPatch(typeof(SkyManager), nameof(SkyManager.UpdateOverlays))]
    static class SkyManagerPatch
    {
        static void Prefix()
        {
            MatBases.SunShadowFade.color = MatBases.SunShadow.color;
        }
    }

    [HarmonyPatch(typeof(LayerSubMesh), nameof(LayerSubMesh.FinalizeMesh))]
    static class LayerSubMeshFinalizePatch
    {
        static void Prefix(LayerSubMesh __instance, ref MeshParts parts)
        {
            if (__instance.material == MatBases.SunShadowFade)
                parts &= ~MeshParts.UVs;
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(Printer_Shadow), nameof(Printer_Shadow.PrintShadow))]
    [HarmonyPatch(new[] { typeof(SectionLayer), typeof(Vector3), typeof(Vector3), typeof(Rot4) })]
    static class PrinterShadowPatch
    {
        static void Postfix(SectionLayer layer, Vector3 center)
        {
            LayerSubMesh mesh = layer.GetSubMesh(MatBases.SunShadowFade);

            if (mesh.verts.Count > 0)
                mesh.uvs.Add(center - PrintPlanePatch.currentThing ?? Vector3.zero);
        }
    }
}
