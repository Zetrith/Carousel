using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Carousel
{
    [HotSwappable]
    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt))]
    static class RenderPawnInternalPatch_HandleRotation
    {
        static void Prefix(PawnRenderer __instance, ref Rot4? rotOverride, ref bool __state)
        {
            var pawn = __instance.pawn;
            if (pawn.Map == null || pawn.GetPosture() != PawnPosture.Standing || pawn.Downed) return;

            HandleRotation(pawn, ref rotOverride);
        }

        static void HandleRotation(Pawn pawn, ref Rot4? rotOverride)
        {
            var comp = pawn.Map.CarouselComp();
            var camera = Rot4.FromAngleFlat(-comp.current).AsInt;

            // Conditions from Pawn_RotationTracker.UpdateRotation
            if (!pawn.Destroyed && !pawn.jobs.HandlingFacing && pawn.pather.Moving && pawn.pather.curPath != null && pawn.pather.curPath.NodesLeftCount >= 1)
            {
                var movingRotation = FaceAdjacentCell(pawn.Position, pawn.pather.nextCell, Rot4.FromAngleFlat(-comp.current));
                if (movingRotation != null)
                {
                    rotOverride = new Rot4(movingRotation.Value.AsInt);
                    return;
                }
            }

            rotOverride = new Rot4((rotOverride?.AsInt ?? pawn.Rotation.AsInt) + camera);
        }

        static Rot4? FaceAdjacentCell(IntVec3 pawn, IntVec3 c, Rot4 cameraRot)
        {
            if (c == pawn)
                return null;

            IntVec3 diff = (c - pawn).RotatedBy(cameraRot);

            if (diff.x > 0)
                return Rot4.East;

            if (diff.x < 0)
                return Rot4.West;

            if (diff.z > 0)
                return Rot4.North;

            return Rot4.South;
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt))]
    static class RenderPawnInternalPatch_SetCenter
    {
        static void Prefix(PawnRenderer __instance, Vector3 drawLoc, ref bool __state)
        {
            var pawn = __instance.pawn;
            if (pawn.Map == null || pawn.GetPosture() != PawnPosture.Standing || pawn.Downed) return;

            if (GraphicsDrawMeshPatch.data == null)
            {
                GraphicsDrawMeshPatch.data = (pawn.Map.CarouselComp().current, drawLoc);
                __state = true;
            }
        }

        static void Postfix(bool __state)
        {
            if (__state)
                GraphicsDrawMeshPatch.data = null;
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.Draw))]
    static class ThingWithCompsDrawPatch
    {
        static void Prefix(Thing __instance)
        {
            if (__instance is Corpse || __instance is Pawn) return;
            GraphicsDrawMeshPatch.data = (__instance.Map.CarouselComp().current, __instance.TrueCenter());
        }

        static void Postfix()
        {
            GraphicsDrawMeshPatch.data = null;
        }
    }

    [HarmonyPatch(typeof(Tornado), nameof(Tornado.Draw))]
    static class TornadoDrawPatch
    {
        static void Prefix(Tornado __instance)
        {
            GraphicsDrawMeshPatch.data = (__instance.Map.CarouselComp().current, new Vector3(__instance.realPosition.x, 0, __instance.realPosition.y));
        }

        static void Postfix()
        {
            GraphicsDrawMeshPatch.data = null;
        }
    }

    [HarmonyPatch]
    static class OverlayDrawerPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(OverlayDrawer)))
                if (m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Thing))
                    yield return m;
        }

        static void Prefix([HarmonyArgument(0)] Thing t)
        {
            GraphicsDrawMeshPatch.data = (Find.CurrentMap.CarouselComp().current, t.TrueCenter());
        }

        static void Postfix()
        {
            GraphicsDrawMeshPatch.data = null;
        }
    }

    [HotSwappable]
    [HarmonyPatch]
    static class GraphicsDrawMeshPatch
    {
        // Rotation angle and center
        public static (float, Vector3)? data;

        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(Graphics)))
                if (m.Name == "DrawMesh" && m.GetParameters().Length == 12 && m.GetParameters()[1].Name == "matrix")
                    yield return m;

            foreach (var m in AccessTools.GetDeclaredMethods(typeof(DrawBatch)))
                if (m.Name == "DrawMesh")
                    yield return m;
        }

        static void Prefix(ref Matrix4x4 matrix, Material material)
        {
            if (data == null || material == MatBases.SunShadowFade) return;

            var rotCenter = data.Value.Item2;
            var current = data.Value.Item1;

            float drawX = matrix.m03;
            float drawZ = matrix.m23;

            matrix.m03 = drawX - rotCenter.x;
            matrix.m23 = drawZ - rotCenter.z;

            matrix = Matrix4x4.Rotate(Quaternion.Euler(0, current, 0)) * matrix;

            matrix.m03 += rotCenter.x;
            matrix.m23 += rotCenter.z;
        }
    }

    [HarmonyPatch(typeof(GenMapUI), nameof(GenMapUI.LabelDrawPosFor), new[] { typeof(Thing), typeof(float) })]
    static class LabelDrawPosPatch
    {
        static void Postfix(ref Vector2 __result, Thing thing, float worldOffsetZ)
        {
            Vector3 drawPos = thing.DrawPos;
            drawPos += new Vector3(0, 0, worldOffsetZ).RotatedBy(Rot4.FromAngleFlat(thing.Map.CarouselComp().current));
            Vector2 vector = Find.Camera.WorldToScreenPoint(drawPos) / Prefs.UIScale;
            vector.y = UI.screenHeight - vector.y;

            __result = vector;
        }
    }
}
