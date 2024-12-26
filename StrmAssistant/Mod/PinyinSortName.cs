using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using System;
using System.Reflection;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public static class PinyinSortName
    {
        private static readonly PatchApproachTracker PatchApproachTracker = new PatchApproachTracker();

        private static MethodInfo _sortNameGetter;

        public static void Initialize()
        {
            try
            {
                var sortNameProperty =
                    typeof(BaseItem).GetProperty("SortName", BindingFlags.Instance | BindingFlags.Public);
                _sortNameGetter = sortNameProperty?.GetGetMethod();
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("PinyinSortName - Patch Init Failed");
                Plugin.Instance.Logger.Debug(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
                PatchApproachTracker.FallbackPatchApproach = PatchApproach.None;
            }

            if (HarmonyMod == null) PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;

            if (PatchApproachTracker.FallbackPatchApproach != PatchApproach.None &&
                Plugin.Instance.MetadataEnhanceStore.GetOptions().PinyinSortName)
            {
                Patch();
            }
        }

        public static void Patch()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (!IsPatched(_sortNameGetter, typeof(PinyinSortName)))
                    {
                        HarmonyMod.Patch(_sortNameGetter,
                            postfix: new HarmonyMethod(typeof(PinyinSortName).GetMethod("SortNameGetterPostfix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug("Patch SortNameGetter Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Patch PinyinSortName Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                    PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;
                }
            }
        }

        public static void Unpatch()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (IsPatched(_sortNameGetter, typeof(PinyinSortName)))
                    {
                        HarmonyMod.Unpatch(_sortNameGetter,
                            AccessTools.Method(typeof(PinyinSortName), "SortNameGetterPostfix"));
                        Plugin.Instance.Logger.Debug("Unpatch SortNameGetter Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Unpatch PinyinSortName Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                }
            }
        }

        [HarmonyPostfix]
        private static void SortNameGetterPostfix(BaseItem __instance, ref string __result)
        {
            if (__instance.SupportsUserData && __instance.EnableAlphaNumericSorting && !(__instance is IHasSeries) &&
                (__instance is Video || __instance is Audio || __instance is IItemByName ||
                 __instance is Folder && !__instance.IsTopParent))
            {
                if (__instance.IsFieldLocked(MetadataFields.SortName) || IsJapanese(__instance.Name) ||
                    !IsChinese(__instance.Name)) return;

                var nameToProcess =
                    __instance is BoxSet ? RemoveDefaultCollectionName(__instance.Name) : __instance.Name;

                __result = ConvertToPinyinInitials(nameToProcess);
            }
        }
    }
}
