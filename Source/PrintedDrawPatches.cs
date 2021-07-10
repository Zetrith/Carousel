using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Verse;

namespace Carousel
{
    [HotSwappable]
    [HarmonyPatch(typeof(SectionLayer), nameof(SectionLayer.DrawLayer))]
    static class SectionLayerDrawPatch
    {
        static MethodBase MatrixIdentity = AccessTools.PropertyGetter(typeof(Matrix4x4), nameof(Matrix4x4.identity));
        static FieldInfo SubMeshMat = AccessTools.Field(typeof(LayerSubMesh), nameof(LayerSubMesh.material));

        static MethodBase OffsetMethod = AccessTools.Method(typeof(SectionLayerDrawPatch), nameof(SectionLayerDrawPatch.AddOffset));
        static MethodBase TransformMaterialMethod = AccessTools.Method(typeof(SectionLayerDrawPatch), nameof(SectionLayerDrawPatch.TransformMaterial));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                yield return inst;

                if (inst.operand == MatrixIdentity)
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

        static Matrix4x4 AddOffset(Matrix4x4 matrix, Material mat, SectionLayer layer)
        {
            if (MaterialAtlasCtor_Patch.linkedCornerMatsSet.Contains(mat))
            {
                matrix = Matrix4x4.Translate(
                    new Vector3(0, 0, -Graphic_LinkedCornerFiller.ShiftUp) +
                    new Vector3(0, 0, Graphic_LinkedCornerFiller.ShiftUp).RotatedBy(layer.Map.CarouselComp().current)
                );
            }

            return matrix;
        }

        // Atlas material => (Linked flags, Original material)
        public static Dictionary<Material, (int, Material)> linkedToSingle = new Dictionary<Material, (int, Material)>();

        public static Material TransformMaterial(Material mat, SectionLayer layer)
        {
            if (LinkedPrintPatch.linkedMaterials.Contains(mat))
                return RemapLinked(mat, Rot4.FromAngleFlat(layer.Map.CarouselComp().set));

            if (GraphicPrintPatch_TransformMats.exchangeMats.TryGetValue(mat, out var data))
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
    static class GraphicPrintPatch_TransformMats
    {
        public static Dictionary<Material, (Graphic_Multi, Rot4)> exchangeMats = new Dictionary<Material, (Graphic_Multi, Rot4)>();
        public static Dictionary<Graphic, Material> copiedForFlip = new Dictionary<Graphic, Material>();

        public static HashSet<Material> graphicSingle = new HashSet<Material>();

        static MethodBase MatAt = AccessTools.Method(typeof(Graphic), nameof(Graphic.MatAt));
        static MethodBase CacheMaterialMethod = AccessTools.Method(typeof(GraphicPrintPatch_TransformMats), nameof(GraphicPrintPatch_TransformMats.CacheMaterial));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                yield return inst;

                if (inst.operand == MatAt)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return new CodeInstruction(OpCodes.Call, CacheMaterialMethod);
                }
            }
        }

        static Material CacheMaterial(Material mat, Graphic graphic, Thing t)
        {
            // For these Graphics, not an actual rotation but a texture switch is used for rotating
            if (ShouldExchangeVerts(graphic))
            {
                var outMat = mat;
                var rot = t.Rotation;

                if (rot == Rot4.East && graphic.EastFlipped || rot == Rot4.West && graphic.WestFlipped)
                {
                    if (!copiedForFlip.TryGetValue(graphic, out outMat))
                    {
                        copiedForFlip[graphic] = outMat = new Material(mat);

                        if (MaterialPool.matDictionaryReverse.TryGetValue(mat, out var matreq))
                            MaterialPool.matDictionaryReverse[outMat] = matreq;
                    }
                }

                exchangeMats[outMat] = ((Graphic_Multi)graphic, rot);

                return outMat;
            }

            if (ShouldRotateVertices(graphic, t))
                graphicSingle.Add(mat);

            return mat;
        }

        public static bool ShouldExchangeVerts(Graphic graphic)
        {
            return graphic.GetType() == typeof(Graphic_Multi) && !graphic.ShouldDrawRotated;
        }

        public static bool ShouldRotateVertices(Graphic graphic, Thing t)
        {
            // If something has a single texture but rotates anyway it means it looks good from every side and
            // doesn't have to rotate with the camera
            return graphic.GetType() == typeof(Graphic_Single) && (!graphic.ShouldDrawRotated || !t.def.rotatable) ||
                graphic.GetType() == typeof(Graphic_Multi) && graphic.ShouldDrawRotated ||
                graphic.GetType() == typeof(Graphic_StackCount) ||
                t.def.category == ThingCategory.Item;
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
            PrintPlanePatch.currentThingCenter = __instance.TrueCenter();
        }

        static void Postfix()
        {
            PrintPlanePatch.currentThingCenter = null;
            printing = false;
        }
    }

    // todo handle MinifiedThings
    [HotSwappable]
    [HarmonyPatch(typeof(Graphic), nameof(Graphic.Print))]
    static class GraphicPrintPatch_SetData
    {
        public static Dictionary<Graphic_Multi, int> graphicToInt = new Dictionary<Graphic_Multi, int>();
        public static List<Graphic_Multi> intToGraphic = new List<Graphic_Multi>();

        static void Prefix(Graphic __instance, Thing thing, ref int __state)
        {
            if (PrintPlanePatch.currentThingCenter == null &&
                GraphicPrintPatch_TransformMats.ShouldRotateVertices(__instance, thing))
            {
                PrintPlanePatch.currentThingCenter = thing.TrueCenter();
                __state |= 1;
            }

            if (PrintPlanePatch.currentThingData == null &&
                GraphicPrintPatch_TransformMats.ShouldExchangeVerts(__instance))
            {
                var multi = (Graphic_Multi)__instance;
                if (!graphicToInt.TryGetValue(multi, out var id))
                {
                    id = intToGraphic.Count;
                    graphicToInt[multi] = id;
                    intToGraphic.Add(multi);
                }

                PrintPlanePatch.currentThingData = new Vector3(
                    PrintPlanePatch.SPECIAL_X,
                    id,
                    ((__instance.WestFlipped ? 2 : 0) + (__instance.EastFlipped ? 1 : 0)) | (thing.Rotation.AsInt << 2)
                );

                __state |= 2;
            }
        }

        static void Postfix(int __state)
        {
            if ((__state & 1) == 1)
                PrintPlanePatch.currentThingCenter = null;

            if ((__state & 2) == 2)
                PrintPlanePatch.currentThingData = null;
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(Printer_Plane), nameof(Printer_Plane.PrintPlane))]
    static class PrintPlanePatch
    {
        public static Vector3? currentThingCenter;
        public static Vector3? currentThingData;

        public static HashSet<Material> plantMats = new HashSet<Material>();

        public const int SPECIAL_X = 9999;
        public const int EMPTY_X = 99999;
        public static readonly Vector3 EMPTY = new Vector3(EMPTY_X, 0, 0);

        static void Prefix(ref Material mat, Vector2[] uvs)
        {
            bool atlasTexture = mat.HasProperty("_MainTex") &&
                BakeStaticAtlasesPatch.atlasTextures.TryGetValue(mat.mainTexture, out var atlasGroup) &&
                RightAtlasGroup(atlasGroup);

            if (uvs == Graphic_LinkedCornerFiller.CornerFillUVs)
                mat = MaterialAtlasCtor_Patch.linkedCornerMats[mat];

            if (PlantPrintPatch.printing && !atlasTexture)
                plantMats.Add(mat);

            var toAdd = currentThingCenter ?? (atlasTexture ? (currentThingData ?? EMPTY) : (Vector3?)null);

            if (toAdd != null)
                FinalizeMeshPatch.dataBuffers.GetOrAdd(mat, k => new List<Vector3>()).Add(toAdd.Value);
        }

        public static bool RightAtlasGroup(TextureAtlasGroup atlasGroup)
        {
            return atlasGroup == TextureAtlasGroup.Plant ||
                atlasGroup == TextureAtlasGroup.Building ||
                atlasGroup == TextureAtlasGroup.Item;
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(SectionLayer), nameof(SectionLayer.FinalizeMesh))]
    static class FinalizeMeshPatch
    {
        public static Dictionary<Material, List<Vector3>> dataBuffers = new Dictionary<Material, List<Vector3>>();

        static void Postfix(SectionLayer __instance)
        {
            if (dataBuffers.Any(kv => kv.Value.Count > 0))
            {
                foreach (var kv in dataBuffers)
                {
                    var mesh = __instance.GetSubMesh(kv.Key);

                    mesh.verts.InsertRange(mesh.verts.Count, kv.Value);
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

    [HarmonyPatch(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases))]
    static class BakeStaticAtlasesPatch
    {
        public static Dictionary<Texture, TextureAtlasGroup> atlasTextures = new Dictionary<Texture, TextureAtlasGroup>();

        static void Postfix()
        {
            atlasTextures.Clear();
            foreach (var atlas in GlobalTextureAtlasManager.staticTextureAtlases)
                atlasTextures[atlas.ColorTexture] = atlas.group;
        }
    }

    // Early patch
    static class MaterialAtlasCtor_Patch
    {
        public static Dictionary<Material, Material> linkedCornerMats = new Dictionary<Material, Material>();
        public static HashSet<Material> linkedCornerMatsSet = new HashSet<Material>();

        public static void Postfix(MaterialAtlasPool.MaterialAtlas __instance)
        {
            foreach (var mat in __instance.subMats)
            {
                linkedCornerMatsSet.Add(linkedCornerMats[mat] = new Material(mat));
            }
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
