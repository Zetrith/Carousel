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
using Verse.AI;

namespace Carousel
{
    [StaticConstructorOnStartup]
    static class Carousel
    {
        public static readonly KeyBindingDef RotateKey = KeyBindingDef.Named("CarouselRotate");
        
        static Carousel()
        {
            // Make SunShadowFade != SunShadow which isn't the case in vanilla
            AccessTools.Field(typeof(MatBases), nameof(MatBases.SunShadowFade))
                .SetValue(null, new Material(MatBases.SunShadowFade));

            CarouselMod.harmony.PatchAll();
        }
    }

    public class CarouselMod : Mod
    {
        public static Harmony harmony = new Harmony("carousel");
        public static Settings settings;

        public CarouselMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<Settings>();

            harmony.Patch(
                AccessTools.Constructor(typeof(MaterialAtlasPool.MaterialAtlas), new[] { typeof(Material) }),
                postfix: new HarmonyMethod(typeof(MaterialAtlasCtor_Patch), nameof(MaterialAtlasCtor_Patch.Postfix))
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

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.ColumnWidth = 220f;

            listing.CheckboxLabeled("Disable compass", ref settings.disableCompass);

            listing.End();
        }

        public override string SettingsCategory()
        {
            return "Carousel";
        }
    }

    public class Settings : ModSettings
    {
        public bool disableCompass;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref disableCompass, "disableCompass");
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

                        if (mesh.material.HasProperty("_MainTex") &&
                            BakeStaticAtlasesPatch.atlasTextures.TryGetValue(mesh.material.mainTexture, out var group) &&
                            PrintPlanePatch.RightAtlasGroup(group))
                        {
                            TransformAtlas(mesh, group);
                            continue;
                        }

                        TransformVerts(mesh);
                        TransformUVs(mesh);

                        if (mesh.material == MatBases.SunShadowFade)
                            TransformShadows(mesh);
                    }
        }

        static Vector3[] tempUVs = new Vector3[0];
        static Vector3[] tempVerts = new Vector3[0];

        void TransformUVs(LayerSubMesh mesh)
        {
            // Fix texture flip
            if (GraphicPrintPatch_TransformMats.exchangeMats.TryGetValue(mesh.material, out var matData))
            {
                var uvsc = mesh.uvs.Count;
                var graphic = matData.Item1;

                var relRot = GenMath.PositiveMod(matData.Item2.AsInt - Rot4.FromAngleFlat(target).AsInt, 4);
                var flipped = relRot == 1 && graphic.EastFlipped || relRot == 3 && graphic.WestFlipped ? 1 : 0;

                Util.ResizeIfNeeded(ref tempUVs, uvsc);

                for (int i = 0; i < uvsc; i += 4)
                    FixUVs(tempUVs, Printer_Plane.defaultUvs, i, flipped);

                mesh.mesh.SetUVs(tempUVs, uvsc);
            }
        }

        void TransformVerts(LayerSubMesh mesh)
        {
            var offset = Rot4.FromAngleFlat(target);
            var offseti = offset.AsInt;

            // Rotate around a center
            if (PrintPlanePatch.plantMats.Contains(mesh.material) ||
                GraphicPrintPatch_TransformMats.graphicSingle.Contains(mesh.material))
            {
                var vertsc = mesh.verts.Count / 5 * 4;
                var vertsArr = NoAllocHelpers.ExtractArrayFromListT(mesh.verts);

                Util.ResizeIfNeeded(ref tempVerts, vertsc);

                for (int i = 0; i < vertsc; i += 4)
                {
                    // The mesh data lists are only used during mesh building and are otherwise unused.
                    // In between mesh rebuilding, Carousel reuses the lists to recalculate the meshes
                    // but also appends additional information to the end of the vertex list
                    var center = vertsArr[vertsc + i / 4];

                    RotateVerts(tempVerts, vertsArr, i, center, offset);
                }

                mesh.mesh.SetVertices(tempVerts, vertsc);
            }

            // Exchange vertices
            // This doesn't change the set of their values but changes their order
            if (GraphicPrintPatch_TransformMats.exchangeMats.ContainsKey(mesh.material) ||
               LinkedPrintPatch.linkedMaterials.Contains(mesh.material))
            {
                var vertsc = mesh.verts.Count;
                var vertsArr = NoAllocHelpers.ExtractArrayFromListT(mesh.verts);

                Util.ResizeIfNeeded(ref tempVerts, vertsc);

                for (int i = 0; i < vertsc; i += 4)
                    ExchangeVerts(tempVerts, vertsArr, i, offseti);

                mesh.mesh.SetVertices(tempVerts, vertsc);
            }
        }

        void TransformAtlas(LayerSubMesh mesh, TextureAtlasGroup group)
        {
            var offset = Rot4.FromAngleFlat(target);
            var offseti = offset.AsInt;
            var vertsc = mesh.verts.Count / 5 * 4;
            var uvsc = mesh.uvs.Count;
            var vertsArr = NoAllocHelpers.ExtractArrayFromListT(mesh.verts);
            var uvsArr = NoAllocHelpers.ExtractArrayFromListT(mesh.uvs);

            Util.ResizeIfNeeded(ref tempVerts, vertsc);
            Util.ResizeIfNeeded(ref tempUVs, uvsc);

            for (int i = 0; i < vertsc; i += 4)
            {
                var data = vertsArr[vertsc + i / 4];

                if (data.x == PrintPlanePatch.SPECIAL_X)
                {
                    ExchangeVerts(tempVerts, vertsArr, i, offseti);

                    var rotData = ((int)data.z & 0b1100) >> 2;
                    var flipData = (int)data.z & 0b0011;

                    var relRot = GenMath.PositiveMod(rotData - Rot4.FromAngleFlat(target).AsInt, 4);
                    var flipped = relRot == 1 && ((flipData & 1) == 1) || relRot == 3 && ((flipData & 2) == 2) ? 1 : 0;

                    var rotatedMat = GraphicPrintPatch_SetData.intToGraphic[(int)data.y].mats[(rotData + Rot4.FromAngleFlat(-target).AsInt) % 4];
                    Graphic.TryGetTextureAtlasReplacementInfo(rotatedMat, group, false, false, out _, out var uvs, out _);

                    FixUVs(
                        tempUVs,
                        uvs,
                        i,
                        flipped
                    );
                }
                else if (data.x != PrintPlanePatch.EMPTY_X)
                {
                    RotateVerts(tempVerts, vertsArr, i, data, offset);
                    Array.Copy(uvsArr, i, tempUVs, i, 4);
                }
                else
                {
                    Array.Copy(vertsArr, i, tempVerts, i, 4);
                    Array.Copy(uvsArr, i, tempUVs, i, 4);
                }
            }

            mesh.mesh.SetVertices(tempVerts, vertsc);
            mesh.mesh.SetUVs(tempUVs, uvsc);
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

        static void FixUVs(Vector3[] tempUVs, Vector2[] origUvs, int i, int flipped)
        {
            tempUVs[i + (0 + 3 * flipped & 3)] = origUvs[0];
            tempUVs[i + (1 + 1 * flipped & 3)] = origUvs[1];
            tempUVs[i + (2 + 3 * flipped & 3)] = origUvs[2];
            tempUVs[i + (3 + 1 * flipped & 3)] = origUvs[3];
        }

        static void ExchangeVerts(Vector3[] tempVerts, Vector3[] vertsArr, int i, int offseti)
        {
            tempVerts[i].SetXZY(ref vertsArr[i + (offseti & 3)], vertsArr[i].y);
            tempVerts[i + 1].SetXZY(ref vertsArr[i + (offseti + 1 & 3)], vertsArr[i + 1].y);
            tempVerts[i + 2].SetXZY(ref vertsArr[i + (offseti + 2 & 3)], vertsArr[i + 2].y);
            tempVerts[i + 3].SetXZY(ref vertsArr[i + (offseti + 3 & 3)], vertsArr[i + 3].y);
        }

        static void RotateVerts(Vector3[] tempVerts, Vector3[] vertsArr, int i, Vector3 c, Rot4 offset)
        {
            tempVerts[i] = c + (vertsArr[i] - c).RotatedBy(offset);
            tempVerts[i + 1] = c + (vertsArr[i + 1] - c).RotatedBy(offset);
            tempVerts[i + 2] = c + (vertsArr[i + 2] - c).RotatedBy(offset);
            tempVerts[i + 3] = c + (vertsArr[i + 3] - c).RotatedBy(offset);
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
