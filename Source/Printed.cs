using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Carousel
{
    [HotSwappable]
    [HarmonyPatch(typeof(SectionLayer), nameof(SectionLayer.DrawLayer))]
    static class SectionLayerDrawPatch
    {
        static MethodBase ZeroGetter = AccessTools.PropertyGetter(typeof(Vector3), nameof(Vector3.zero));
        static FieldInfo SubMeshMat = AccessTools.Field(typeof(LayerSubMesh), nameof(LayerSubMesh.material));

        static MethodBase OffsetMethod = AccessTools.Method(typeof(SectionLayerDrawPatch), nameof(SectionLayerDrawPatch.AddOffset));
        static MethodBase TransformMaterialMethod = AccessTools.Method(typeof(SectionLayerDrawPatch), nameof(SectionLayerDrawPatch.TransformMaterial));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                yield return inst;

                if (inst.operand == ZeroGetter)
                {
                    yield return new CodeInstruction(OpCodes.Ldloc_2);
                    yield return new CodeInstruction(OpCodes.Ldfld, SubMeshMat);
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, OffsetMethod);
                }

                if (inst.operand == SubMeshMat)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, TransformMaterialMethod);
                }
            }
        }

        static Vector3 AddOffset(Vector3 v, Material mat, SectionLayer layer)
        {
            if (CarouselMod.linkedCornerMatsSet.Contains(mat))
            {
                v.z -= Graphic_LinkedCornerFiller.ShiftUp;
                v += new Vector3(0, 0, Graphic_LinkedCornerFiller.ShiftUp).RotatedBy(layer.Map.CarouselComp().current);
            }

            return v;
        }

        // Atlas material => (Linked flags, Original material)
        public static Dictionary<Material, (int, Material)> linkedToSingle = new Dictionary<Material, (int, Material)>();

        public static Material TransformMaterial(Material mat, SectionLayer layer)
        {
            if (LinkedPrintPatch.linkedMaterials.Contains(mat))
                return RemapLinked(mat, Rot4.FromAngleFlat(layer.Map.CarouselComp().set));

            if (GraphicPrintPatch.matData.TryGetValue(mat, out var data))
                return data.Item1.mats[(data.Item2.AsInt + Rot4.FromAngleFlat(-layer.Map.CarouselComp().set).AsInt) % 4];

            return mat;
        }

        static Material RemapLinked(Material mat, Rot4 cameraRot)
        {
            var single = linkedToSingle.GetOrAdd(mat, InitLinkedToSingle);
            var offset = cameraRot.AsInt;

            return MaterialAtlasPool.SubMaterialFromAtlas(
                single.Item2,
                (LinkDirections)(((single.Item1 >> offset) | (single.Item1 << (4 - offset))) & 0xf)
            );
        }

        static (int, Material) InitLinkedToSingle(Material mat)
        {
            foreach (var kv in MaterialAtlasPool.atlasDict)
            {
                var index = -1;
                for (int i = 0; i < kv.Value.subMats.Length; i++)
                    if (kv.Value.subMats[i] == mat)
                        index = i;

                if (index != -1)
                    return linkedToSingle[mat] = (index, kv.Key);
            }

            throw new Exception($"Can't map linked material {mat}");
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(Graphic), nameof(Graphic.Print))]
    static class GraphicPrintPatch
    {
        public static Dictionary<Material, (Graphic_Multi, Rot4)> matData = new Dictionary<Material, (Graphic_Multi, Rot4)>();
        public static Dictionary<Graphic, Material> copiedForFlip = new Dictionary<Graphic, Material>();

        public static HashSet<Material> graphicSingle = new HashSet<Material>();

        static MethodBase MatAt = AccessTools.Method(typeof(Graphic), nameof(Graphic.MatAt));
        static MethodBase TransformMaterialMethod = AccessTools.Method(typeof(GraphicPrintPatch), nameof(GraphicPrintPatch.TransformMaterial));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                yield return inst;

                if (inst.operand == MatAt)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return new CodeInstruction(OpCodes.Call, TransformMaterialMethod);
                }
            }
        }

        static Material TransformMaterial(Material mat, Graphic graphic, Thing t)
        {
            // For these Graphics, not an actual rotation but a texture switch is used for rotating
            if (graphic.GetType() == typeof(Graphic_Multi) && !graphic.ShouldDrawRotated)
            {
                var outMat = mat;
                var rot = t.Rotation;

                if (rot == Rot4.East && graphic.EastFlipped || rot == Rot4.West && graphic.WestFlipped)
                {
                    if (!copiedForFlip.TryGetValue(graphic, out outMat))
                        copiedForFlip[graphic] = outMat = new Material(mat);
                }

                matData[outMat] = ((Graphic_Multi)graphic, rot);

                return outMat;
            }

            if (ShouldRotateVertices(graphic, t))
                graphicSingle.Add(mat);

            return mat;
        }

        public static bool ShouldRotateVertices(Graphic graphic, Thing t)
        {
            // If something has a single texture but rotates anyway it means it looks good from every side and
            // doesn't have to rotate with the camera
            return graphic.GetType() == typeof(Graphic_Single) && (!graphic.ShouldDrawRotated || !t.def.rotatable) ||
                graphic.GetType() == typeof(Graphic_Multi) && graphic.ShouldDrawRotated ||
                graphic.GetType() == typeof(Graphic_Random);
        }
    }

    [HarmonyPatch(typeof(Graphic_Linked), nameof(Graphic_Linked.Print))]
    static class LinkedPrintPatch
    {
        public static HashSet<Material> linkedMaterials = new HashSet<Material>();

        static void Prefix(Graphic_Linked __instance, Thing thing)
        {
            linkedMaterials.Add(__instance.LinkedDrawMatFrom(thing, thing.Position));
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(Plant), nameof(Plant.Print))]
    static class PlantPrintPatch
    {
        public static bool printing;

        static void Prefix(Plant __instance)
        {
            printing = true;
            PrintPlanePatch.currentThing = __instance.TrueCenter();
        }

        static void Postfix()
        {
            PrintPlanePatch.currentThing = null;
            printing = false;
        }
    }

    [HarmonyPatch(typeof(Graphic), nameof(Graphic.Print))]
    static class GraphicPrintPatch_SetCenter
    {
        static void Prefix(Graphic __instance, Thing thing, ref bool __state)
        {
            if (GraphicPrintPatch.ShouldRotateVertices(__instance, thing) && PrintPlanePatch.currentThing == null)
            {
                PrintPlanePatch.currentThing = thing.TrueCenter();
                __state = true;
            }
        }

        static void Postfix(bool __state)
        {
            if (__state)
                PrintPlanePatch.currentThing = null;
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(Printer_Plane), nameof(Printer_Plane.PrintPlane))]
    static class PrintPlanePatch
    {
        public static Vector3? currentThing;
        public static HashSet<Material> plantMats = new HashSet<Material>();

        static void Prefix(ref Material mat, Vector2[] uvs)
        {
            if (uvs == Graphic_LinkedCornerFiller.CornerFillUVs)
                mat = CarouselMod.linkedCornerMats[mat];

            if (PlantPrintPatch.printing)
                plantMats.Add(mat);

            if (currentThing != null)
            {
                var list = FinalizeMeshPatch.centerBuffers.GetOrAdd(mat, k => new List<Vector3>());

                var center = currentThing.Value;
                list.Add(center);
            }
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(SectionLayer), nameof(SectionLayer.FinalizeMesh))]
    static class FinalizeMeshPatch
    {
        public static Dictionary<Material, List<Vector3>> centerBuffers = new Dictionary<Material, List<Vector3>>();

        static void Postfix(SectionLayer __instance)
        {
            if (centerBuffers.Any(kv => kv.Value.Count > 0))
            {
                foreach (var kv in centerBuffers)
                {
                    var mesh = __instance.GetSubMesh(kv.Key);

                    NoAllocHelpers.ResizeList(mesh.uvs, 0);
                    mesh.uvs.InsertRange(0, kv.Value);
                    NoAllocHelpers.ResizeList(kv.Value, 0);
                }
            }
        }
    }

    [HotSwappable]
    [HarmonyPatch]
    static class SectionRegeneratePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Section), nameof(Section.RegenerateAllLayers));
            yield return AccessTools.Method(typeof(Section), nameof(Section.RegenerateLayers));
        }

        static void Postfix(Section __instance)
        {
            var comp = __instance.map.CarouselComp();

            if (comp.progress < CarouselMapComp.CameraUpdateTime &&
                comp.CameraRect().Contains(new IntVec3(__instance.botLeft.x / 17, 0, __instance.botLeft.z / 17)))
                return;

            comp.UpdateSection(__instance);
        }
    }

    // Early patch
    static class GraphicInitMatFromPatch
    {
        static MethodBase MatFrom = AccessTools.Method(typeof(MaterialPool), nameof(MaterialPool.MatFrom), new[] { typeof(MaterialRequest) });
        static MethodBase TransformRequestMethod = AccessTools.Method(typeof(GraphicInitMatFromPatch), nameof(TransformRequest));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.operand == MatFrom)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, TransformRequestMethod);
                }

                yield return inst;
            }
        }

        static Dictionary<(GraphicData, List<ShaderParameter>), List<ShaderParameter>> shaderParamMap = new Dictionary<(GraphicData, List<ShaderParameter>), List<ShaderParameter>>();

        static MaterialRequest TransformRequest(MaterialRequest req, GraphicRequest graphicRequest)
        {
            req.shaderParameters = shaderParamMap.GetOrAdd(
                (graphicRequest.graphicData, req.shaderParameters),
                // Create a copy of the list so the MatRequest hashcode/equals is different and a new material
                // is created for every Graphic instance even when the material parameters are the same.
                // Materials are used to distinguish objects and have to be separated for different ones.
                k => k.Item2 == null ? new List<ShaderParameter>() : new List<ShaderParameter>(k.Item2)
            );

            return req;
        }
    }

}
