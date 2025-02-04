﻿using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnableImageCapture : PatchBase<EnableImageCapture>
    {
        private static ConstructorInfo _staticConstructor;
        private static FieldInfo _resourcePoolField;
        private static MethodInfo _isShortcutGetter;
        private static PropertyInfo _isShortcutProperty;
        private static MethodInfo _supportsImageCapture;
        private static MethodInfo _getImage;
        private static MethodInfo _runExtraction;
        private static Type _quickSingleImageExtractor;
        private static PropertyInfo _totalTimeoutMs;
        private static MethodInfo _supportsThumbnailsGetter;
        private static Type _quickImageSeriesExtractor;
        private static MethodInfo _logThumbnailImageExtractionFailure;

        private static readonly AsyncLocal<BaseItem> ShortcutItem = new AsyncLocal<BaseItem>();
        private static readonly AsyncLocal<BaseItem> ImageCaptureItem = new AsyncLocal<BaseItem>();
        private static int _isShortcutPatchUsageCount;

        private static SemaphoreSlim SemaphoreFFmpeg;
        public static int SemaphoreFFmpegMaxCount { get; private set; }

        public EnableImageCapture()
        {
            SemaphoreFFmpegMaxCount = Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount;

            Initialize();

            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture)
            {
                SemaphoreFFmpeg = new SemaphoreSlim(SemaphoreFFmpegMaxCount);
                PatchResourcePool();
                var resourcePool = (SemaphoreSlim)_resourcePoolField?.GetValue(null);
                Plugin.Instance.Logger.Info(
                    "Current FFmpeg ResourcePool: " + resourcePool?.CurrentCount ?? string.Empty);

                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
            var imageExtractorBaseType =
                mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.ImageExtraction.ImageExtractorBase");
            _staticConstructor = imageExtractorBaseType.GetConstructor(BindingFlags.Static | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            _resourcePoolField =
                imageExtractorBaseType.GetField("resourcePool", BindingFlags.NonPublic | BindingFlags.Static);
            _isShortcutGetter = typeof(BaseItem).GetProperty("IsShortcut", BindingFlags.Instance | BindingFlags.Public)
                ?.GetGetMethod();
            _isShortcutProperty =
                typeof(BaseItem).GetProperty("IsShortcut", BindingFlags.Instance | BindingFlags.Public);

            var embyProviders = Assembly.Load("Emby.Providers");
            var videoImageProvider = embyProviders.GetType("Emby.Providers.MediaInfo.VideoImageProvider");
            _supportsImageCapture =
                videoImageProvider.GetMethod("Supports", BindingFlags.Instance | BindingFlags.Public);
            _getImage = videoImageProvider.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "GetImage")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();

            var supportsThumbnailsProperty =
                typeof(Video).GetProperty("SupportsThumbnails", BindingFlags.Public | BindingFlags.Instance);
            _supportsThumbnailsGetter = supportsThumbnailsProperty?.GetGetMethod();
            _runExtraction =
                imageExtractorBaseType.GetMethod("RunExtraction", BindingFlags.Instance | BindingFlags.Public);
            _quickSingleImageExtractor =
                mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.ImageExtraction.QuickSingleImageExtractor");
            _totalTimeoutMs = _quickSingleImageExtractor.GetProperty("TotalTimeoutMs");
            _quickImageSeriesExtractor =
                mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.ImageExtraction.QuickImageSeriesExtractor");

            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var sqliteItemRepository =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
            _logThumbnailImageExtractionFailure = sqliteItemRepository.GetMethod("LogThumbnailImageExtractionFailure",
                BindingFlags.Public | BindingFlags.Instance);
        }

        protected override void Prepare(bool apply)
        {
            PatchOrUnpatchIsShortcut(apply);

            PatchUnpatch(PatchTracker, apply, _supportsImageCapture, prefix: nameof(SupportsImageCapturePrefix),
                postfix: nameof(SupportsImageCapturePostfix));
            PatchUnpatch(PatchTracker, apply, _getImage, prefix: nameof(GetImagePrefix));
            PatchUnpatch(PatchTracker, apply, _supportsThumbnailsGetter,
                prefix: nameof(SupportsThumbnailsGetterPrefix), postfix: nameof(SupportsThumbnailsGetterPostfix));
            PatchUnpatch(PatchTracker, apply, _runExtraction, prefix: nameof(RunExtractionPrefix));
            PatchUnpatch(PatchTracker, apply, _logThumbnailImageExtractionFailure,
                prefix: nameof(LogThumbnailImageExtractionFailurePrefix));
        }

        private static void PatchResourcePool()
        {
            var result = PatchUnpatch(Instance.PatchTracker, true, _staticConstructor,
                prefix: nameof(ResourcePoolPrefix));
            //var result = PatchUnpatch(Instance.PatchTracker, true, _staticConstructor,
            //    transpiler: nameof(ResourcePoolTranspiler));

            if (!result && Instance.PatchTracker.FallbackPatchApproach == PatchApproach.Reflection)
                PatchResourcePoolByReflection();
        }

        private static void PatchResourcePoolByReflection()
        {
            //works only with modded Emby.Server.MediaEncoding.dll

            try
            {
                _resourcePoolField.SetValue(null, SemaphoreFFmpeg);
                Plugin.Instance.Logger.Debug("Patch FFmpeg ResourcePool Success by Reflection");
            }
            catch (Exception re)
            {
                Plugin.Instance.Logger.Debug("Patch FFmpeg ResourcePool Failed by Reflection");
                Plugin.Instance.Logger.Debug(re.Message);
                Instance.PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }
        }

        private void UnpatchResourcePool()
        {
            PatchUnpatch(PatchTracker, false, _staticConstructor, prefix: nameof(ResourcePoolPrefix));
            //PatchUnpatch(PatchTracker, false, _staticConstructor, transpiler: nameof(ResourcePoolTranspiler));

            var resourcePool = (SemaphoreSlim)_resourcePoolField.GetValue(null);
            Plugin.Instance.Logger.Info("Current FFmpeg Resource Pool: " + resourcePool?.CurrentCount ?? string.Empty);
        }

        public static void UpdateResourcePool(int maxConcurrentCount)
        {
            if (SemaphoreFFmpegMaxCount != maxConcurrentCount)
            {
                SemaphoreFFmpegMaxCount = maxConcurrentCount;
                SemaphoreSlim newSemaphoreFFmpeg;
                SemaphoreSlim oldSemaphoreFFmpeg;

                switch (Instance.PatchTracker.FallbackPatchApproach)
                {
                    case PatchApproach.Harmony:
                        Plugin.Instance.ApplicationHost.NotifyPendingRestart();

                        /* un-patch and re-patch don't work for readonly static field
                        UnpatchResourcePool();

                        _currentMaxConcurrentCount = maxConcurrentCount;
                        newSemaphoreFFmpeg = new SemaphoreSlim(maxConcurrentCount);
                        oldSemaphoreFFmpeg = SemaphoreFFmpeg;
                        SemaphoreFFmpeg = newSemaphoreFFmpeg;

                        PatchResourcePool();

                        oldSemaphoreFFmpeg.Dispose();
                        */
                        break;

                    case PatchApproach.Reflection:

                        newSemaphoreFFmpeg = new SemaphoreSlim(maxConcurrentCount);
                        oldSemaphoreFFmpeg = SemaphoreFFmpeg;
                        SemaphoreFFmpeg = newSemaphoreFFmpeg;

                        PatchResourcePoolByReflection();

                        oldSemaphoreFFmpeg.Dispose();
                        
                        break;
                }
            }

            var resourcePool = (SemaphoreSlim)_resourcePoolField.GetValue(null);
            Plugin.Instance.Logger.Info("Current FFmpeg ResourcePool: " + resourcePool?.CurrentCount ?? string.Empty);
        }

        public static void PatchOrUnpatchIsShortcut(bool apply)
        {
            PatchUnpatch(Instance.PatchTracker, apply, _isShortcutGetter, ref _isShortcutPatchUsageCount,
                prefix: nameof(IsShortcutPrefix));
        }

        public static void AllowImageCaptureInstance(BaseItem item)
        {
            ImageCaptureItem.Value = item;
        }

        public static void PatchIsShortcutInstance(BaseItem item)
        {
            switch (Instance.PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    ShortcutItem.Value = item;
                    break;

                case PatchApproach.Reflection:
                    try
                    {
                        _isShortcutProperty.SetValue(item, true); //special logic depending on modded MediaBrowser.Controller.dll
                        //Plugin.Instance.Logger.Debug("Patch IsShortcut Success by Reflection" + " - " + item.Name + " - " + item.Path);
                    }
                    catch (Exception re)
                    {
                        Plugin.Instance.Logger.Debug("Patch IsShortcut Failed by Reflection");
                        Plugin.Instance.Logger.Debug(re.Message);
                        Instance.PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    }
                    break;
            }
        }

        public static void UnpatchIsShortcutInstance(BaseItem item)
        {
            switch (Instance.PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    ShortcutItem.Value = null;
                    break;

                case PatchApproach.Reflection:
                    try
                    {
                        _isShortcutProperty.SetValue(item, false); //special logic depending on modded MediaBrowser.Controller.dll
                        //Plugin.Instance.Logger.Debug("Unpatch IsShortcut Success by Reflection" + " - " + item.Name + " - " + item.Path);
                    }
                    catch (Exception re)
                    {
                        Plugin.Instance.Logger.Debug("Unpatch IsShortcut Failed by Reflection");
                        Plugin.Instance.Logger.Debug(re.Message);
                    }
                    break;
            }
        }
        
        [HarmonyPrefix]
        private static bool ResourcePoolPrefix()
        {
            _resourcePoolField.SetValue(null, SemaphoreFFmpeg);
            return false;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ResourcePoolTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Ldc_I4_1)
                {
                    codes[i] = new CodeInstruction(OpCodes.Ldc_I4_S,
                        (sbyte)Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount);
                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Ldc_I4_1)
                    {
                        codes[i + 1] = new CodeInstruction(OpCodes.Ldc_I4_S,
                            (sbyte)Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount);
                    }
                    break;
                }
            }
            return codes.AsEnumerable();
        }

        [HarmonyPrefix]
        private static bool IsShortcutPrefix(BaseItem __instance, ref bool __result)
        {
            if (ShortcutItem.Value != null && __instance.InternalId == ShortcutItem.Value.InternalId)
            {
                __result = false;
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool SupportsImageCapturePrefix(BaseItem item, ref bool __result)
        {
            if (ImageCaptureItem.Value != null && ImageCaptureItem.Value.InternalId == item.InternalId)
            {
                PatchIsShortcutInstance(item);
            }

            return true;
        }

        [HarmonyPostfix]
        private static void SupportsImageCapturePostfix(BaseItem item, ref bool __result)
        {
            if (ImageCaptureItem.Value != null && ImageCaptureItem.Value.InternalId == item.InternalId)
            {
                UnpatchIsShortcutInstance(item);
            }
        }

        [HarmonyPrefix]
        private static bool GetImagePrefix(ref BaseMetadataResult itemResult)
        {
            if (itemResult != null && itemResult.MediaStreams != null)
            {
                itemResult.MediaStreams = itemResult.MediaStreams
                    .Where(ms => ms.Type != MediaStreamType.EmbeddedImage)
                    .ToArray();
            }

            return true;
        }

        private static bool IsFileShortcut(string path)
        {
            return path != null && string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        private static long GetThumbnailPositionTicks(long runtimeTicks)
        {
            var percent = Plugin.Instance.MediaInfoExtractStore.GetOptions().ImageCapturePosition / 100.0;

            var min = Math.Min(Convert.ToInt64(runtimeTicks * 0.5), TimeSpan.FromSeconds(20.0).Ticks);

            return Math.Max(Convert.ToInt64(runtimeTicks * percent), min);
        }

        [HarmonyPrefix]
        private static bool RunExtractionPrefix(object __instance, ref string inputPath, MediaContainers? container,
            MediaStream videoStream, MediaProtocol? protocol, int? streamIndex, Video3DFormat? threedFormat,
            ref TimeSpan? startOffset, TimeSpan? interval, string targetDirectory, string targetFilename, int? maxWidth,
            bool enableThumbnailFilter, CancellationToken cancellationToken)
        {
            if (__instance.GetType() == _quickImageSeriesExtractor && IsFileShortcut(inputPath))
            {
                var strmPath = inputPath;
                inputPath = Task.Run(async () => await Plugin.LibraryApi.GetStrmMountPath(strmPath)).Result;
            }

            if (ImageCaptureItem.Value != null && _totalTimeoutMs != null &&
                __instance.GetType() == _quickSingleImageExtractor)
            {
                var newValue =
                    60000 * Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount;
                _totalTimeoutMs.SetValue(__instance, newValue);

                var timeSpan =
                    ImageCaptureItem.Value.MediaContainer.GetValueOrDefault() == MediaContainers.Dvd ||
                    !ImageCaptureItem.Value.RunTimeTicks.HasValue || ImageCaptureItem.Value.RunTimeTicks.Value <= 0L
                        ? TimeSpan.FromSeconds(10.0)
                        : TimeSpan.FromTicks(GetThumbnailPositionTicks(ImageCaptureItem.Value.RunTimeTicks.Value));

                startOffset = timeSpan;

                ImageCaptureItem.Value = null;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool LogThumbnailImageExtractionFailurePrefix(long itemId, long dateModifiedUnixTimeSeconds)
        {
            return false;
        }

        [HarmonyPrefix]
        private static bool SupportsThumbnailsGetterPrefix(BaseItem __instance, ref bool __result, out bool __state)
        {
            __state = false;

            if (__instance.IsShortcut)
            {
                PatchIsShortcutInstance(__instance);
                __state = true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void SupportsThumbnailsGetterPostfix(BaseItem __instance, ref bool __result, bool __state)
        {
            if (__state)
            {
                UnpatchIsShortcutInstance(__instance);
            }
        }
    }
}
