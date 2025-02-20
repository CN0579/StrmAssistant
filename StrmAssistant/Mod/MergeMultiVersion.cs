using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using System;
using System.IO;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.ExperienceEnhanceOptions;

namespace StrmAssistant.Mod
{
    public class MergeMultiVersion : PatchBase<MergeMultiVersion>
    {
        private static MethodInfo _isEligibleForMultiVersion;
        private static MethodInfo _addLibrariesToPresentationUniqueKey;

        public MergeMultiVersion()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().MergeMultiVersion)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var namingAssembly = Assembly.Load("Emby.Naming");
            var videoListResolverType = namingAssembly.GetType("Emby.Naming.Video.VideoListResolver");
            _isEligibleForMultiVersion = videoListResolverType.GetMethod("IsEligibleForMultiVersion",
                BindingFlags.Static | BindingFlags.NonPublic);
            _addLibrariesToPresentationUniqueKey = typeof(Series).GetMethod("AddLibrariesToPresentationUniqueKey",
                BindingFlags.NonPublic | BindingFlags.Instance);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _isEligibleForMultiVersion,
                prefix: nameof(IsEligibleForMultiVersionPrefix));
            PatchUnpatch(PatchTracker, apply, _addLibrariesToPresentationUniqueKey,
                prefix: nameof(AddLibrariesToPresentationUniqueKeyPrefix));
        }

        [HarmonyPrefix]
        private static bool IsEligibleForMultiVersionPrefix(string folderName, string testFilename, ref bool __result)
        {
            __result = string.Equals(folderName, Path.GetFileName(Path.GetDirectoryName(testFilename)),
                StringComparison.OrdinalIgnoreCase);

            return false;
        }

        [HarmonyPrefix]
        private static bool AddLibrariesToPresentationUniqueKeyPrefix(Series __instance, string key,
            ref BaseItem[] collectionFolders, LibraryOptions libraryOptions)
        {
            var globalScope = Plugin.Instance.ExperienceEnhanceStore.GetOptions()
                .MergeSeriesPreference == MergeScopeOption.GlobalScope;

            if (globalScope)
            {
                var allCollectionFolders = Plugin.LibraryApi.GetAllCollectionFolders(__instance);

                if (allCollectionFolders.Length > 0)
                {
                    collectionFolders = allCollectionFolders;
                }
            }

            return true;
        }
    }
}
