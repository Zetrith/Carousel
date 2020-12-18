using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Carousel
{
    [StaticConstructorOnStartup]
    static class Carousel
    {
        public static KeyBindingDef RotateKey = KeyBindingDef.Named("CarouselRotate");

        static Carousel()
        {
            // Differentiate from SunShadow. In vanilla, SunShadowFade = SunShadow
            AccessTools.Field(typeof(MatBases), nameof(MatBases.SunShadowFade))
                .SetValue(null, new Material(MatBases.SunShadowFade));

            CarouselMod.harmony.PatchAll();
        }
    }

    public class CarouselMod : Mod
    {
        public static Harmony harmony = new Harmony("carousel");

        public CarouselMod(ModContentPack content) : base(content)
        {
            harmony.Patch(
                AccessTools.Constructor(typeof(MaterialAtlasPool.MaterialAtlas), new[] { typeof(Material) }),
                postfix: new HarmonyMethod(typeof(CarouselMod), nameof(CarouselMod.MaterialAtlasCtorPostfix))
            );

            harmony.Patch(
                AccessTools.Method(typeof(Graphic_Single), nameof(Graphic_Single.Init)),
                transpiler: new HarmonyMethod(typeof(GraphicInitMatFromPatch), nameof(GraphicInitMatFromPatch.Transpiler))
            );

            harmony.Patch(
               AccessTools.Method(typeof(Graphic_Multi), nameof(Graphic_Multi.Init)),
               transpiler: new HarmonyMethod(typeof(GraphicInitMatFromPatch), nameof(GraphicInitMatFromPatch.Transpiler))
           );
        }

        public static Dictionary<Material, Material> linkedCornerMats = new Dictionary<Material, Material>();
        public static HashSet<Material> linkedCornerMatsSet = new HashSet<Material>();

        public static void MaterialAtlasCtorPostfix(MaterialAtlasPool.MaterialAtlas __instance)
        {
            foreach (var mat in __instance.subMats)
            {
                linkedCornerMatsSet.Add(linkedCornerMats[mat] = new Material(mat));
            }
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(MainButtonsRoot), nameof(MainButtonsRoot.MainButtonsOnGUI))]
    public static class MainButtonsPatch
    {
        static void Prefix()
        {
            if (Carousel.RotateKey.KeyDownEvent && Find.CurrentMap != null)
            {
                Find.CurrentMap.CarouselComp().RotateBy(Event.current.shift ? -90f : 90f);
                Event.current.Use();
            }
        }
    }

    [HotSwappable]
    public class CarouselMapComp : MapComponent
    {
        public float current;
        public float start;
        public float target;
        public float set;
        public int progress;

        int sectionsDone;
        int workerIndex;
        IntVec3 cameraPos;

        public const int AnimTime = 8;
        public const int CameraUpdateTime = 5;

        public CarouselMapComp(Map map) : base(map)
        {
        }

        public void RotateBy(float by)
        {
            current = target;
            start = current;
            progress = 0;
            sectionsDone = 0;
            workerIndex = 0;
            cameraPos = Find.CameraDriver.MapPosition;

            target += by;
            target = GenMath.PositiveMod(target, 360f);

            //Log.Message($"{Time.frameCount} {target} {current}");
        }

        public void Update()
        {
            var dist = Mathf.DeltaAngle(start, target);
            current = GenMath.PositiveMod(start + progress * progress * dist / (AnimTime * AnimTime), 360f);

            if (current != target)
                AnimationStep();

            if (Find.CurrentMap == map)
                Find.Camera.transform.rotation = Quaternion.Euler(90, current, 0);
        }

        void AnimationStep()
        {
            var watch = Stopwatch.StartNew();
            var drawer = map.mapDrawer;
            var mapRect = MapRect();
            var cameraRect = CameraRect();

            var c = Math.Max(drawer.sections.Length - cameraRect.Area, 0);
            var step = Mathf.CeilToInt((float)c / (AnimTime - 1));
            var goal = Math.Min(c, sectionsDone + step);

            if (progress == CameraUpdateTime)
            {
                foreach (var cell in cameraRect)
                    if (mapRect.Contains(cell))
                        UpdateSection(drawer.sections[cell.x, cell.z]);

                set = target;
            }
            else
            {
                while (sectionsDone < goal)
                {
                    var cell = new IntVec3(workerIndex % mapRect.Width, 0, workerIndex / mapRect.Height);

                    if (!cameraRect.Contains(cell))
                    {
                        UpdateSection(drawer.sections[cell.x, cell.z]);
                        sectionsDone++;
                    }

                    workerIndex++;
                }
            }

            progress++;

            //Log.Message($"{current} {start} {Mathf.DeltaAngle(start, target)} {progress} {sectionsDone} {workerIndex} {step} {c} {goal} {watch.ElapsedMilliseconds}");
        }

        public CellRect MapRect()
        {
            var drawer = map.mapDrawer;
            return new CellRect(0, 0, drawer.SectionCount.x, drawer.SectionCount.z);
        }

        public CellRect CameraRect()
        {
            var cameraSection = map.mapDrawer.SectionCoordsAt(cameraPos);
            return new CellRect(cameraSection.x - 2, cameraSection.z - 1, 4, 3).Inside(MapRect());
        }

        public void UpdateSection(Section section)
        {
            foreach (var layer in section.layers)
                if (layer.relevantChangeTypes.HasFlag(MapMeshFlag.Buildings) || layer.relevantChangeTypes.HasFlag(MapMeshFlag.Things))
                    foreach (var mesh in layer.subMeshes)
                    {
                        if (mesh.verts.Count == 0) continue;

                        if (mesh.verts.Count % 4 == 0)
                        {
                            TransformUVs(mesh);
                            TransformVerts(mesh);
                        }

                        if (mesh.material == MatBases.SunShadowFade)
                            TransformShadows(mesh);
                    }
        }

        static Vector3[] tempUVs = new Vector3[4];
        static Vector3[] tempVerts = new Vector3[4];

        void TransformUVs(LayerSubMesh mesh)
        {
            // Fix texture flip
            if (GraphicPrintPatch.matData.TryGetValue(mesh.material, out var matData))
            {
                var fullRot = GenMath.PositiveMod(matData.Item2.AsInt - Rot4.FromAngleFlat(target).AsInt, 4);
                var graphic = matData.Item1;
                var flipped = fullRot == 1 && graphic.EastFlipped || fullRot == 3 && graphic.WestFlipped ? 1 : 0;

                var origUvs = Printer_Plane.defaultUvs;
                var uvsc = mesh.uvs.Count;

                Util.ResizeIfNeeded(ref tempUVs, uvsc);

                for (int i = 0; i < uvsc; i += 4)
                {
                    tempUVs[i + (3 * flipped & 3)] = origUvs[0];
                    tempUVs[i + (1 + flipped & 3)] = origUvs[1];
                    tempUVs[i + (2 + 3 * flipped & 3)] = origUvs[2];
                    tempUVs[i + (3 + flipped & 3)] = origUvs[3];
                }

                mesh.mesh.SetUVs(tempUVs, uvsc);
            }
        }

        void TransformVerts(LayerSubMesh mesh)
        {
            var offset = Rot4.FromAngleFlat(target);
            var offseti = offset.AsInt;

            var uvs = mesh.uvs;
            var vertsc = mesh.verts.Count;
            var origVerts = NoAllocHelpers.ExtractArrayFromListT(mesh.verts);

            // Rotate around a center
            if (PrintPlanePatch.plantMats.Contains(mesh.material) ||
                GraphicPrintPatch.graphicSingle.Contains(mesh.material))
            {
                if (uvs.Count * 4 != vertsc)
                {
                    Log.ErrorOnce($"Carousel: Bad material {mesh.material}", mesh.material.GetHashCode());
                    return;
                }

                Util.ResizeIfNeeded(ref tempVerts, vertsc);

                for (int i = 0; i < vertsc; i += 4)
                {
                    // The mesh data lists are only used during mesh building and are otherwise unused
                    // In between mesh rebuilding, Carousel uses the uv list to store object centers for rotation
                    var c = uvs[i / 4];

                    tempVerts[i] = c + (origVerts[i] - c).RotatedBy(offset);
                    tempVerts[i + 1] = c + (origVerts[i + 1] - c).RotatedBy(offset);
                    tempVerts[i + 2] = c + (origVerts[i + 2] - c).RotatedBy(offset);
                    tempVerts[i + 3] = c + (origVerts[i + 3] - c).RotatedBy(offset);
                }

                mesh.mesh.SetVertices(tempVerts, vertsc);
            }

            // Exchange vertices
            if (GraphicPrintPatch.matData.ContainsKey(mesh.material) ||
               LinkedPrintPatch.linkedMaterials.Contains(mesh.material))
            {
                Util.ResizeIfNeeded(ref tempVerts, vertsc);

                for (int i = 0; i < vertsc; i += 4)
                {
                    tempVerts[i].SetXZY(ref origVerts[i + (offseti & 3)], origVerts[i].y);
                    tempVerts[i + 1].SetXZY(ref origVerts[i + (offseti + 1 & 3)], origVerts[i + 1].y);
                    tempVerts[i + 2].SetXZY(ref origVerts[i + (offseti + 2 & 3)], origVerts[i + 2].y);
                    tempVerts[i + 3].SetXZY(ref origVerts[i + (offseti + 3 & 3)], origVerts[i + 3].y);
                }

                mesh.mesh.SetVertices(tempVerts, vertsc);
            }
        }

        void TransformShadows(LayerSubMesh mesh)
        {
            if (mesh.uvs.Count == 0) return;

            var rot = Rot4.FromAngleFlat(target);
            var uvs = mesh.uvs;
            var vertsc = mesh.verts.Count;
            var origVerts = NoAllocHelpers.ExtractArrayFromListT(mesh.verts);

            Util.ResizeIfNeeded(ref tempVerts, vertsc);

            for (int i = 0; i <= vertsc - 5; i += 5)
            {
                var offset = uvs[i / 10];
                if (offset == Vector3.zero)
                {
                    Array.Copy(origVerts, i, tempVerts, i, 5);
                    continue;
                }

                var r = offset.RotatedBy(rot) - offset;

                tempVerts[i] = origVerts[i] + r;
                tempVerts[i + 1] = origVerts[i + 1] + r;
                tempVerts[i + 2] = origVerts[i + 2] + r;
                tempVerts[i + 3] = origVerts[i + 3] + r;
                tempVerts[i + 4] = origVerts[i + 4] + r;
            }

            mesh.mesh.SetVertices(tempVerts, vertsc);
        }

        public override void ExposeData()
        {
             // Prevent loading errors when the mod is removed
            if (Scribe.mode == LoadSaveMode.Saving)
                Scribe.saver.WriteAttribute("IsNull", "True");
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    static class MapUpdatePatch
    {
        static void Prefix(Map __instance)
        {
            __instance.CarouselComp().Update();
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class HotSwappableAttribute : Attribute
    {
    }
}
