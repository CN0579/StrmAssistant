﻿using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using StrmAssistant.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class UnlockIntroSkip : PatchBase<UnlockIntroSkip>
    {
        private static MethodInfo _isIntroDetectionSupported;
        private static MethodInfo _createQueryForEpisodeIntroDetection;
        private static MethodInfo _onFailedToFindIntro;
        private static MethodInfo _detectSequences;
        private static PropertyInfo _confidenceProperty;

        private static readonly AsyncLocal<bool> LogZeroConfidence = new AsyncLocal<bool>();

        public UnlockIntroSkip()
        {
            Initialize();

            if (Plugin.Instance.IntroSkipStore.GetOptions().UnlockIntroSkip)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyProviders = Assembly.Load("Emby.Providers");
            var audioFingerprintManager = embyProviders.GetType("Emby.Providers.Markers.AudioFingerprintManager");
            _isIntroDetectionSupported = audioFingerprintManager.GetMethod("IsIntroDetectionSupported",
                BindingFlags.Public | BindingFlags.Instance);
            var markerScheduledTask = embyProviders.GetType("Emby.Providers.Markers.MarkerScheduledTask");
            _createQueryForEpisodeIntroDetection = markerScheduledTask.GetMethod(
                "CreateQueryForEpisodeIntroDetection",
                BindingFlags.Public | BindingFlags.Static);

            var sequenceDetection = embyProviders.GetType("Emby.Providers.Markers.SequenceDetection");
            _detectSequences = sequenceDetection.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "DetectSequences" && m.GetParameters().Length == 8);
            var sequenceDetectionResult = embyProviders.GetType("Emby.Providers.Markers.SequenceDetectionResult");
            _confidenceProperty =
                sequenceDetectionResult.GetProperty("Confidence", BindingFlags.Public | BindingFlags.Instance);
            _onFailedToFindIntro = audioFingerprintManager.GetMethod("OnFailedToFindIntro",
                BindingFlags.NonPublic | BindingFlags.Static);
        }

        protected override void Prepare(bool apply)
        {
            EnableImageCapture.PatchOrUnpatchIsShortcut(apply);

            PatchUnpatch(PatchTracker, apply, _isIntroDetectionSupported,
                prefix: nameof(IsIntroDetectionSupportedPrefix), postfix: nameof(IsIntroDetectionSupportedPostfix));
            PatchUnpatch(PatchTracker, apply, _createQueryForEpisodeIntroDetection,
                postfix: nameof(CreateQueryForEpisodeIntroDetectionPostfix));
            PatchUnpatch(PatchTracker, apply, _detectSequences, postfix: nameof(DetectSequencesPostfix));
            PatchUnpatch(PatchTracker, apply, _onFailedToFindIntro, prefix: nameof(OnFailedToFindIntroPrefix));
        }

        [HarmonyPrefix]
        private static bool IsIntroDetectionSupportedPrefix(Episode item, LibraryOptions libraryOptions,
            ref bool __result, out bool __state)
        {
            __state = false;

            if (item.IsShortcut)
            {
                EnableImageCapture.PatchIsShortcutInstance(item);
                __state = true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void IsIntroDetectionSupportedPostfix(Episode item, LibraryOptions libraryOptions,
            ref bool __result, bool __state)
        {
            if (__state)
            {
                EnableImageCapture.UnpatchIsShortcutInstance(item);
            }
        }

        [HarmonyPostfix]
        private static void CreateQueryForEpisodeIntroDetectionPostfix(LibraryOptions libraryOptions, ref InternalItemsQuery __result)
        {
            var markerEnabledLibraryScope = Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope;

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
            {
                __result.ParentIds = Plugin.FingerprintApi.GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (FingerprintApi.LibraryPathsInScope.Any())
                {
                    __result.PathStartsWithAny = FingerprintApi.LibraryPathsInScope.ToArray();
                }
                
                var blackListSeasons = Plugin.FingerprintApi.GetAllBlacklistSeasons().ToArray();
                if (blackListSeasons.Any())
                {
                    __result.ExcludeParentIds = blackListSeasons;
                }
            }
        }

        [HarmonyPostfix]
        private static void DetectSequencesPostfix(object __result)
        {
            if (_confidenceProperty.GetValue(__result) is double confidence && confidence == 0)
            {
                LogZeroConfidence.Value = true;
            }
        }

        [HarmonyPrefix]
        private static bool OnFailedToFindIntroPrefix(Episode episode)
        {
            if (LogZeroConfidence.Value)
            {
                BaseItem.ItemRepository.LogIntroDetectionFailureFailure(episode.InternalId,
                    episode.DateModified.ToUnixTimeSeconds());

                _ = Plugin.LibraryApi.SerializeMediaInfo(episode.InternalId, true, "Zero Fingerprint Confidence",
                    CancellationToken.None);
            }

            return false;
        }
    }
}
