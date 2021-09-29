using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace Carousel
{
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
    static class CurrentViewRectPatch
    {
        private static int lastViewRectGetFrame = -1;
        private static CellRect lastViewRect;

        static void Postfix(ref CellRect __result)
        {
            if (Find.CurrentMap == null) return;

            if (Time.frameCount != lastViewRectGetFrame)
            {
                var center = __result.CenterVector3;
                var corners = __result.Corners.Select(c => (c.ToVector3Shifted() - center).RotatedBy(-Find.CurrentMap.CarouselComp().current) + center);
                var min = corners.Aggregate((a, b) => Vector3.Min(a, b));
                var max = corners.Aggregate((a, b) => Vector3.Max(a, b));

                lastViewRectGetFrame = Time.frameCount;
                lastViewRect = CellRect.FromLimits(FloorVec(min), CeilVec(max));
            }

            __result = lastViewRect;
        }

        static IntVec3 FloorVec(Vector3 v) => new IntVec3(Mathf.FloorToInt(v.x), Mathf.FloorToInt(v.y), Mathf.FloorToInt(v.z));

        static IntVec3 CeilVec(Vector3 v) => new IntVec3(Mathf.CeilToInt(v.x), Mathf.CeilToInt(v.y), Mathf.CeilToInt(v.z));
    }

    [HarmonyPatch(typeof(UnityGUIBugsFixer), nameof(UnityGUIBugsFixer.FixDelta))]
    static class FixDeltaPatch
    {
        static void Postfix()
        {
            if (Find.CurrentMap != null)
                UnityGUIBugsFixer.currentEventDelta = UnityGUIBugsFixer.currentEventDelta.RotatedBy(Find.CurrentMap.CarouselComp().current);
        }
    }

    [HarmonyPatch(typeof(UI), nameof(UI.CurUICellSize))]
    static class CurUICellSizePatch
    {
        static void Postfix(ref float __result)
        {
            __result = Math.Abs((new Vector3(1f, 0f, 1f).MapToUIPosition() - new Vector3(0f, 0f, 0f).MapToUIPosition()).x);
        }
    }

    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CalculateCurInputDollyVect))]
    static class InputDollyPatch
    {
        static void Postfix(ref Vector2 __result)
        {
            __result = __result.RotatedBy(-Find.CurrentMap.CarouselComp().current);
        }
    }

    [HarmonyPatch(typeof(ThingSelectionUtility), nameof(ThingSelectionUtility.GetMapRect))]
    static class GetMapRectPatch
    {
        static void Postfix(ref CellRect __result)
        {
            __result = CellRect.FromLimits(__result.minX, __result.minZ, __result.maxX, __result.maxZ);
        }
    }
}
