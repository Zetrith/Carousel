using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Carousel
{
    [HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.TotalHeightAt))]
    static class MakeSpaceForCompass
    {
        static void Postfix(GameConditionManager __instance, ref float __result)
        {
            if (__instance != Find.CurrentMap?.gameConditionManager) return;
            __result += 84f;
        }
    }

    [HotSwappable]
    [HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.DoConditionsUI))]
    static class DrawCompass
    {
        static void Prefix(GameConditionManager __instance, Rect rect)
        {
            if (__instance != Find.CurrentMap?.gameConditionManager) return;

            var comp = Find.CurrentMap.CarouselComp();
            var center = new Vector2(UI.screenWidth - 10f - 32f, rect.yMax - 10f - 32f);
            Widgets.DrawTextureRotated(center, CompassWidget.CompassTex, -comp.current - 90f, 1f);

            Rect btnRect = new Rect(center.x - 32f, center.y - 32f, 64f, 64f);

            TooltipHandler.TipRegion(
                btnRect,
                () => $"{"CompassTip".Translate()}\n\n{Carousel.RotateKey.LabelCap}: {Carousel.RotateKey.MainKeyLabel}",
                5799998
            );

            if (Widgets.ButtonInvisible(btnRect, true))
                comp.RotateBy(-comp.current);
        }
    }
}
