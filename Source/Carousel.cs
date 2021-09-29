using System;
using System.Collections.Generic;
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
    public static class HandleCameraKey
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

    [AttributeUsage(AttributeTargets.Class)]
    public class HotSwappableAttribute : Attribute
    {
    }
}
