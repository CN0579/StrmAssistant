using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Mod
{
    public class ExclusiveExtract : PatchBase<ExclusiveExtract>
    {
        internal class RefreshContext
        {
            public long InternalId { get; set; }
            public MetadataRefreshOptions MetadataRefreshOptions { get; set; }
            public bool IsNewItem { get; set; }
            public bool IsScanning { get; set; }
            public bool IsPlayback { get; set; }
            public bool IsFileChanged { get; set; }
            public bool IsExternalSubtitleChanged { get; set; }
            public bool IsPersistInScope { get; set; }
            public bool MediaInfoUpdated { get; set; }
            public bool HasMetadataFetchers { get; set; }
        }

        private static MethodInfo _canRefreshMetadata;
        private static MethodInfo _canRefreshImage;
        private static MethodInfo _isSaverEnabledForItem;
        private static MethodInfo _afterMetadataRefresh;
        private static MethodInfo _runFfProcess;
        private static PropertyInfo _standardOutput;
        private static PropertyInfo _standardError;

        private static MethodInfo _addVirtualFolder;
        private static MethodInfo _removeVirtualFolder;
        private static MethodInfo _addMediaPath;
        private static MethodInfo _removeMediaPath;

        private static MethodInfo _saveChapters;
        private static MethodInfo _deleteChapters;

        private static MethodInfo _getRefreshOptions;

        private static readonly AsyncLocal<long> ExclusiveItem = new AsyncLocal<long>();
        private static readonly AsyncLocal<long> ProtectIntroItem = new AsyncLocal<long>();

        private static AsyncLocal<RefreshContext> CurrentRefreshContext { get; } = new AsyncLocal<RefreshContext>();

        public ExclusiveExtract()
        {
            Initialize();

            PatchFfProbeProcess();

            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().ExclusiveExtract)
            {
                UpdateExclusiveControlFeatures(Plugin.Instance.MediaInfoExtractStore.GetOptions()
                    .ExclusiveControlFeatures);
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyProviders = Assembly.Load("Emby.Providers");
            var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
            _canRefreshMetadata = providerManager.GetMethod("CanRefresh", BindingFlags.Static | BindingFlags.NonPublic);
            _canRefreshImage = providerManager.GetMethod("CanRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
            _isSaverEnabledForItem =
                providerManager.GetMethod("IsSaverEnabledForItem", BindingFlags.Instance | BindingFlags.NonPublic);
            _afterMetadataRefresh =
                typeof(BaseItem).GetMethod("AfterMetadataRefresh", BindingFlags.Instance | BindingFlags.Public);

            var mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
            var mediaProbeManager =
                mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
            _runFfProcess =
                mediaProbeManager.GetMethod("RunFfProcess", BindingFlags.Instance | BindingFlags.NonPublic);
            var processRunAssembly = Assembly.Load("Emby.ProcessRun");
            var processResult = processRunAssembly.GetType("Emby.ProcessRun.Common.ProcessResult");
            _standardOutput = processResult.GetProperty("StandardOutput");
            _standardError = processResult.GetProperty("StandardError");

            var embyApi = Assembly.Load("Emby.Api");
            var libraryStructureService = embyApi.GetType("Emby.Api.Library.LibraryStructureService");
            _addVirtualFolder = libraryStructureService.GetMethod("Post",
                new[] { embyApi.GetType("Emby.Api.Library.AddVirtualFolder") });
            _removeVirtualFolder = libraryStructureService.GetMethod("Any",
                new[] { embyApi.GetType("Emby.Api.Library.RemoveVirtualFolder") });
            _addMediaPath = libraryStructureService.GetMethod("Post",
                new[] { embyApi.GetType("Emby.Api.Library.AddMediaPath") });
            _removeMediaPath = libraryStructureService.GetMethod("Any",
                new[] { embyApi.GetType("Emby.Api.Library.RemoveMediaPath") });
            
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var sqliteItemRepository =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
            _saveChapters = sqliteItemRepository.GetMethod("SaveChapters",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(long), typeof(bool), typeof(List<ChapterInfo>) }, null);
            _deleteChapters =
                sqliteItemRepository.GetMethod("DeleteChapters", BindingFlags.Instance | BindingFlags.Public);

            var itemRefreshService = embyApi.GetType("Emby.Api.ItemRefreshService");
            _getRefreshOptions =
                itemRefreshService.GetMethod("GetRefreshOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _canRefreshImage, prefix: nameof(CanRefreshImagePrefix));
            PatchUnpatch(PatchTracker, apply, _canRefreshMetadata, prefix: nameof(CanRefreshMetadataPrefix),
                postfix: nameof(CanRefreshMetadataPostfix));
            PatchUnpatch(PatchTracker, apply, _isSaverEnabledForItem, prefix: nameof(IsSaverEnabledForItemPrefix));
            PatchUnpatch(PatchTracker, apply, _afterMetadataRefresh, prefix: nameof(AfterMetadataRefreshPrefix));
            PatchUnpatch(PatchTracker, apply, _addVirtualFolder, prefix: nameof(RefreshLibraryPrefix));
            PatchUnpatch(PatchTracker, apply, _removeVirtualFolder, prefix: nameof(RefreshLibraryPrefix));
            PatchUnpatch(PatchTracker, apply, _addMediaPath, prefix: nameof(RefreshLibraryPrefix));
            PatchUnpatch(PatchTracker, apply, _removeMediaPath, prefix: nameof(RefreshLibraryPrefix));
            PatchUnpatch(PatchTracker, apply, _saveChapters, prefix: nameof(SaveChaptersPrefix));
            PatchUnpatch(PatchTracker, apply, _deleteChapters, prefix: nameof(DeleteChaptersPrefix));
            PatchUnpatch(PatchTracker, apply, _getRefreshOptions, postfix: nameof(GetRefreshOptionsPostfix));
        }

        private void PatchFfProbeProcess()
        {
            PatchUnpatch(PatchTracker, true, _runFfProcess, prefix: nameof(RunFfProcessPrefix),
                postfix: nameof(RunFfProcessPostfix));
        }

        public static void AllowExtractInstance(BaseItem item)
        {
            if (!IsExclusiveFeatureSelected(ExclusiveControl.NoIntroProtect) &&
                item.DateLastRefreshed != DateTimeOffset.MinValue && item is Episode &&
                Plugin.ChapterApi.HasIntro(item))
            {
                ProtectIntroItem.Value = item.InternalId;
            }

            ExclusiveItem.Value = item.InternalId;
        }

        [HarmonyPrefix]
        private static void RunFfProcessPrefix(ref int timeoutMs)
        {
            if (ExclusiveItem.Value != 0)
            {
                timeoutMs = 60000 * Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount;
            }
        }

        [HarmonyPostfix]
        private static void RunFfProcessPostfix(ref object __result)
        {
            if (__result is Task task)
            {
                var result = task.GetType().GetProperty("Result")?.GetValue(task);

                if (result != null)
                {
                    var standardOutput = _standardOutput.GetValue(result) as string;
                    var standardError = _standardError.GetValue(result) as string;

                    if (standardOutput != null && standardError != null)
                    {
                        var partialOutput = standardOutput.Length > 20
                            ? standardOutput.Substring(0, 20)
                            : standardOutput;

                        if (Regex.Replace(partialOutput, @"\s+", "") == "{}")
                        {
                            var lines = standardError.Split(new[] { '\r', '\n' },
                                StringSplitOptions.RemoveEmptyEntries);

                            if (lines.Length > 0)
                            {
                                var errorMessage = lines[lines.Length - 1].Trim();

                                Plugin.Instance.Logger.Error("MediaInfoExtract - FfProbe Error: " + errorMessage);
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPrefix]
        private static bool CanRefreshImagePrefix(IImageProvider provider, BaseItem item, LibraryOptions libraryOptions,
            ImageRefreshOptions refreshOptions, bool ignoreMetadataLock, bool ignoreLibraryOptions, ref bool __result)
        {
            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId)
            {
                return true;
            }

            if ((item.Parent is null && item.ExtraType is null) ||
                (!(provider is IDynamicImageProviderWithLibraryOptions) && !provider.Supports(item)) ||
                !(item is Video || item is Audio))
            {
                return true;
            }

            if (refreshOptions is MetadataRefreshOptions options)
            {
                if (CurrentRefreshContext.Value is null)
                {
                    CurrentRefreshContext.Value = new RefreshContext
                    {
                        InternalId = item.InternalId,
                        MetadataRefreshOptions = options,
                        IsNewItem = item.DateLastRefreshed == DateTimeOffset.MinValue,
                        IsScanning = options.MetadataRefreshMode <= MetadataRefreshMode.Default &&
                                     options.ImageRefreshMode <= MetadataRefreshMode.Default,
                        HasMetadataFetchers = libraryOptions.TypeOptions.Any(t =>
                            t.Type == item.GetType().Name && t.MetadataFetchers.Any())
                    };

                    if (!CurrentRefreshContext.Value.IsNewItem)
                    {
                        if (options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh &&
                            options.ImageRefreshMode == MetadataRefreshMode.Default &&
                            !options.ReplaceAllMetadata && !options.ReplaceAllImages)
                        {
                            CurrentRefreshContext.Value.IsPlayback = true;
                        }

                        if (Plugin.LibraryApi.HasFileChanged(item, options.DirectoryService))
                        {
                            CurrentRefreshContext.Value.IsFileChanged = true;
                        }

                        if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreExtSubChange) &&
                            item is Video && Plugin.SubtitleApi.HasExternalSubtitleChanged(item, options.DirectoryService))
                        {
                            CurrentRefreshContext.Value.IsExternalSubtitleChanged = true;
                        }

                        if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange) &&
                            IsExclusiveFeatureSelected(ExclusiveControl.ExtractOnFileChange) &&
                            CurrentRefreshContext.Value.IsFileChanged && Plugin.LibraryApi.HasMediaInfo(item) ||
                            IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow))
                        {
                            options.EnableRemoteContentProbe = true;
                            EnableImageCapture.AllowImageCaptureInstance(item);
                        }
                    }
                }
                
                if (CurrentRefreshContext.Value.IsNewItem)
                {
                    return true;
                }

                if (item.HasImage(ImageType.Primary) && (provider is IDynamicImageProvider &&
                        provider.GetType().Name == "VideoImageProvider" || provider is IRemoteImageProvider) &&
                    (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) ||
                     !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) &&
                     !options.ReplaceAllImages))
                {
                    __result = false;
                    return false;
                }
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool CanRefreshMetadataPrefix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock, ref bool __result, out bool __state)
        {
            __state = false;
            
            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId)
            {
                return true;
            }

            if ((item.Parent is null && item.ExtraType is null) || !(provider is IPreRefreshProvider) ||
                !(provider is ICustomMetadataProvider<Video>))
            {
                return true;
            }
            
            if (CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == item.InternalId)
            {
                __state = true;

                if (CurrentRefreshContext.Value.IsNewItem)
                {
                    __result = false;
                    return false;
                }

                var refreshOptions = CurrentRefreshContext.Value.MetadataRefreshOptions;

                if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange) &&
                    CurrentRefreshContext.Value.IsFileChanged ||
                    IsExclusiveFeatureSelected(ExclusiveControl.ExtractAlternative))
                {
                    return true;
                }

                if (CurrentRefreshContext.Value.IsScanning ||
                    !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && refreshOptions.SearchResult != null)
                {
                    __result = false;
                    return false;
                }

                if (!IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) && !item.IsShortcut &&
                    refreshOptions.ReplaceAllImages)
                {
                    return true;
                }

                if (!IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && Plugin.LibraryApi.HasMediaInfo(item))
                {
                    __result = false;
                    return false;
                }

                if (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) ||
                    !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) && !item.IsShortcut)
                {
                    return true;
                }

                if (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) &&
                    !CurrentRefreshContext.Value.IsPlayback)
                {
                    return false;
                }

                return true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void CanRefreshMetadataPostfix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock, ref bool __result, bool __state)
        {
            if (!__state) return;

            var isPersistInScope = !IsExclusiveFeatureSelected(ExclusiveControl.NoPersistIntegration) &&
                                   Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfo &&
                                   (item is Video || item is Audio);
            CurrentRefreshContext.Value.IsPersistInScope = isPersistInScope;

            if (__result)
            {
                if (!IsExclusiveFeatureSelected(ExclusiveControl.NoIntroProtect) &&
                    (IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange) ||
                     !CurrentRefreshContext.Value.IsFileChanged) && item is Episode &&
                    Plugin.ChapterApi.HasIntro(item))
                {
                    ProtectIntroItem.Value = item.InternalId;
                }

                if (isPersistInScope)
                {
                    ChapterChangeTracker.BypassInstance(item);
                    CurrentRefreshContext.Value.MediaInfoUpdated = true;
                }
            }
            else if (CurrentRefreshContext.Value.IsExternalSubtitleChanged)
            {
                _ = Plugin.SubtitleApi.UpdateExternalSubtitles(item, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        [HarmonyPrefix]
        private static void IsSaverEnabledForItemPrefix(IMetadataSaver saver, BaseItem item,
            LibraryOptions libraryOptions, ref ItemUpdateType updateType, bool includeDisabled, bool log,
            ref bool __result)
        {
            if ((updateType & ItemUpdateType.MetadataDownload) == 0) return;

            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId)
            {
                updateType &= ~ItemUpdateType.MetadataDownload;
            }

            if (CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == item.InternalId)
            {
                if (!CurrentRefreshContext.Value.IsNewItem && CurrentRefreshContext.Value.IsScanning)
                {
                    updateType &= ~ItemUpdateType.MetadataDownload;
                }
                else if (!CurrentRefreshContext.Value.HasMetadataFetchers)
                {
                    updateType &= ~ItemUpdateType.MetadataDownload;
                }
            }
        }

        [HarmonyPrefix]
        private static bool AfterMetadataRefreshPrefix(BaseItem __instance)
        {
            if (CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == __instance.InternalId)
            {
                var refreshOptions = CurrentRefreshContext.Value.MetadataRefreshOptions;
                var directoryService = refreshOptions.DirectoryService;

                if (CurrentRefreshContext.Value.IsFileChanged)
                {
                    Plugin.LibraryApi.UpdateDateModifiedLastSaved(__instance, directoryService);
                }

                if (CurrentRefreshContext.Value.IsPersistInScope)
                {
                    
                    if (CurrentRefreshContext.Value.MediaInfoUpdated)
                    {
                        if (__instance.IsShortcut && !refreshOptions.EnableRemoteContentProbe)
                        {
                            if (!CurrentRefreshContext.Value.IsFileChanged)
                            {
                                _ = Plugin.MediaInfoApi.DeserializeMediaInfo(__instance, directoryService,
                                    "Exclusive Restore", CancellationToken.None);
                            }
                            else if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange))
                            {
                                Plugin.MediaInfoApi.DeleteMediaInfoJson(__instance, directoryService,
                                    "Exclusive Delete on Change");
                            }
                        }
                        else
                        {
                            _ = Plugin.MediaInfoApi.SerializeMediaInfo(__instance, directoryService, true,
                                "Exclusive Overwrite", CancellationToken.None);
                        }
                    }
                    else if (!CurrentRefreshContext.Value.IsNewItem)
                    {
                        if (!Plugin.LibraryApi.HasMediaInfo(__instance))
                        {
                            _ = Plugin.MediaInfoApi.DeserializeMediaInfo(__instance, directoryService, "Exclusive Restore",
                                CancellationToken.None);
                        }
                        else
                        {
                            _ = Plugin.MediaInfoApi.SerializeMediaInfo(__instance, directoryService, false,
                                "Exclusive Non-existent", CancellationToken.None);
                        }
                    }
                }
            }

            CurrentRefreshContext.Value = null;

            return true;
        }

        [HarmonyPrefix]
        private static void RefreshLibraryPrefix(IReturnVoid request)
        {
            Traverse.Create(request).Property("RefreshLibrary").SetValue(false);
        }

        [HarmonyPrefix]
        private static bool SaveChaptersPrefix(long itemId, bool clearExtractionFailureResult,
            List<ChapterInfo> chapters)
        {
            if (ProtectIntroItem.Value != 0 && ProtectIntroItem.Value == itemId) return false;

            return true;
        }

        [HarmonyPrefix]
        private static bool DeleteChaptersPrefix(long itemId, MarkerType[] markerTypes)
        {
            if (ProtectIntroItem.Value != 0 && ProtectIntroItem.Value == itemId) return false;

            return true;
        }

        [HarmonyPostfix]
        private static void GetRefreshOptionsPostfix(IReturnVoid request, MetadataRefreshOptions __result)
        {
            var id = Traverse.Create(request).Property("Id").GetValue<string>();

            Plugin.MediaInfoApi.QueueRefreshAlternateVersions(id, __result, true);
        }
    }
}
