﻿using Emby.Naming.Common;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class BeautifyMissingMetadata : PatchBase<BeautifyMissingMetadata>
    {
        private static MethodInfo _getBaseItemDtos;
        private static MethodInfo _getBaseItemDto;

        private static MethodInfo _getMainExpression;
        private static readonly string SeasonNumberAndEpisodeNumberExpression =
            "(?<![a-z]|[0-9])(?<seasonnumber>[0-9]+)(?:[ ._x-]*e|x|[ ._-]*ep[._ -]*|[ ._-]*episode[._ -]+)";

        public BeautifyMissingMetadata()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().UIFunctionOptions.BeautifyMissingMetadata)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var dtoService =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Dto.DtoService");
            _getBaseItemDtos = dtoService.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "GetBaseItemDtos").OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();
            _getBaseItemDto = dtoService.GetMethod("GetBaseItemDto", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(BaseItem), typeof(DtoOptions), typeof(User) }, null);

            _getMainExpression =
                typeof(NamingOptions).GetMethod("GetMainExpression", BindingFlags.NonPublic | BindingFlags.Static);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getBaseItemDtos, postfix: nameof(GetBaseItemDtosPostfix));
            PatchUnpatch(PatchTracker, apply, _getBaseItemDto, postfix: nameof(GetBaseItemDtoPostfix));
            PatchUnpatch(PatchTracker, apply, _getMainExpression, postfix: nameof(GetMainExpressionPostfix));
        }

        [HarmonyPostfix]
        private static void GetBaseItemDtosPostfix(BaseItem[] items, ref BaseItemDto[] __result)
        {
            if (items.Length == 0) return;

            var checkItem = items.FirstOrDefault();

            if (!(checkItem is Episode episode) || !episode.GetPreferredMetadataLanguage()
                    .Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return;

            var episodes = !string.IsNullOrEmpty(checkItem.FileNameWithoutExtension)
                ? items
                : Plugin.LibraryApi.GetItemsByIds(items.Select(i => i.InternalId).ToArray());

            foreach (var (currentItem, index) in episodes.Select((currentItem, index) => (currentItem, index)))
            {
                if (currentItem.IndexNumber.HasValue && string.Equals(currentItem.Name,
                        currentItem.FileNameWithoutExtension, StringComparison.Ordinal))
                {
                    var matchItem = __result[index];
                    matchItem.Name = $"第 {currentItem.IndexNumber} 集";
                }
            }
        }

        [HarmonyPostfix]
        private static void GetBaseItemDtoPostfix(BaseItem item, DtoOptions options, User user,
            ref BaseItemDto __result)
        {
            if (item is Episode && item.IndexNumber.HasValue &&
                item.GetPreferredMetadataLanguage().Equals("zh-CN", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, item.FileNameWithoutExtension, StringComparison.Ordinal))
            {
                __result.Name = $"第 {item.IndexNumber} 集";
            }
        }

        [HarmonyPostfix]
        private static void GetMainExpressionPostfix(ref string __result, bool allowEpisodeNumberOnly,
            bool allowMultiEpisodeNumberOnlyExpression, bool allowX)
        {
            if (allowEpisodeNumberOnly && !allowMultiEpisodeNumberOnlyExpression && allowX)
            {
                __result = Regex.Replace(__result, Regex.Escape(SeasonNumberAndEpisodeNumberExpression) + @"\|?", "");
            }
        }
    }
}
