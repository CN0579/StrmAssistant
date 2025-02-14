﻿using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.UIFunctionOptions;

namespace StrmAssistant.Options.Store
{
    public class ExperienceEnhanceOptionsStore : SimpleFileStore<ExperienceEnhanceOptions>
    {
        private readonly ILogger _logger;

        private bool _currentSuppressOnOptionsSaved;

        public ExperienceEnhanceOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            FileSaving += OnFileSaving;
            FileSaved += OnFileSaved;
        }
        
        public ExperienceEnhanceOptions ExperienceEnhanceOptions => GetOptions();

        public void SavePluginOptionsSuppress()
        {
            _currentSuppressOnOptionsSaved = true;
            SetOptions(ExperienceEnhanceOptions);
        }

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is ExperienceEnhanceOptions options)
            {
                if (string.IsNullOrEmpty(options.UIFunctionOptions.HidePersonPreference))
                {
                    options.UIFunctionOptions.HidePersonPreference = HidePersonOption.NoImage.ToString();
                }

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(ExperienceEnhanceOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (options.IsModSupported &&
                    changedProperties.Contains(nameof(ExperienceEnhanceOptions.MergeMultiVersion)))
                {
                    if (options.MergeMultiVersion)
                    {
                        PatchManager.MergeMultiVersion.Patch();
                    }
                    else
                    {
                        PatchManager.MergeMultiVersion.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.EnhanceNotificationSystem)))
                {
                    if (options.EnhanceNotificationSystem)
                    {
                        PatchManager.EnhanceNotificationSystem.Patch();
                    }
                    else
                    {
                        PatchManager.EnhanceNotificationSystem.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.HidePersonNoImage)))
                {
                    if (options.UIFunctionOptions.HidePersonNoImage)
                    {
                        PatchManager.HidePersonNoImage.Patch();
                    }
                    else
                    {
                        PatchManager.HidePersonNoImage.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.EnforceLibraryOrder)))
                {
                    if (options.UIFunctionOptions.EnforceLibraryOrder)
                    {
                        PatchManager.EnforceLibraryOrder.Patch();
                    }
                    else
                    {
                        PatchManager.EnforceLibraryOrder.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.BeautifyMissingMetadata)))
                {
                    if (options.UIFunctionOptions.BeautifyMissingMetadata)
                    {
                        PatchManager.BeautifyMissingMetadata.Patch();
                    }
                    else
                    {
                        PatchManager.BeautifyMissingMetadata.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.EnhanceMissingEpisodes)))
                {
                    if (options.UIFunctionOptions.EnhanceMissingEpisodes)
                    {
                        PatchManager.EnhanceMissingEpisodes.Patch();
                    }
                    else
                    {
                        PatchManager.EnhanceMissingEpisodes.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.NoBoxsetsAutoCreation)))
                {
                    if (options.UIFunctionOptions.NoBoxsetsAutoCreation)
                    {
                        PatchManager.NoBoxsetsAutoCreation.Patch();
                    }
                    else
                    {
                        PatchManager.NoBoxsetsAutoCreation.Unpatch();
                    }
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is ExperienceEnhanceOptions options)
            {
                var suppress = _currentSuppressOnOptionsSaved;

                if (!suppress)
                {
                    _logger.Info("MergeMultiVersion is set to {0}", options.MergeMultiVersion);
                    _logger.Info("MergeMultiVersionPreferences is set to {0}",
                        options.MergeMultiVersionPreferences.GetDescription());
                    _logger.Info("EnhanceNotificationSystem is set to {0}", options.EnhanceNotificationSystem);
                    _logger.Info("HidePersonNoImage is set to {0}", options.UIFunctionOptions.HidePersonNoImage);
                    var hidePersonPreference = string.Join(", ",
                        options.UIFunctionOptions.HidePersonPreference
                            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s =>
                                Enum.TryParse(s.Trim(), true, out HidePersonOption option)
                                    ? option.GetDescription()
                                    : null)
                            .Where(d => d != null) ?? Enumerable.Empty<string>());
                    _logger.Info("HidePersonPreference is set to {0}", hidePersonPreference);
                    _logger.Info("EnforceLibraryOrder is set to {0}", options.UIFunctionOptions.EnforceLibraryOrder);
                    _logger.Info("BeautifyMissingMetadata is set to {0}",
                        options.UIFunctionOptions.BeautifyMissingMetadata);
                    _logger.Info("EnhanceMissingEpisodes is set to {0}",
                        options.UIFunctionOptions.EnhanceMissingEpisodes);
                    _logger.Info("NoBoxsetsAutoCreation is set to {0}",
                        options.UIFunctionOptions.NoBoxsetsAutoCreation);
                }

                if (suppress) _currentSuppressOnOptionsSaved = false;
            }
        }
    }
}
