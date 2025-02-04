﻿using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using StrmAssistant.Common;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Options.Store
{
    public class PluginOptionsStore : SimpleFileStore<PluginOptions>
    {
        private readonly ILogger _logger;

        private bool _currentSuppressOnOptionsSaved;

        public PluginOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            FileSaved += OnFileSaved;
            FileSaving += OnFileSaving;
        }

        public PluginOptions PluginOptions => GetOptions();

        public void SavePluginOptionsSuppress()
        {
            _currentSuppressOnOptionsSaved = true;
            SetOptions(PluginOptions);
        }
        
        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is PluginOptions options)
            {
                var suppress = _currentSuppressOnOptionsSaved;

                if (string.IsNullOrEmpty(options.GeneralOptions.CatchupTaskScope))
                {
                    options.GeneralOptions.CatchupTaskScope = CatchupTask.MediaInfo.ToString();
                }
                else if (!Plugin.Instance.IntroSkipStore.GetOptions().UnlockIntroSkip)
                {
                    var taskScope = options.GeneralOptions.CatchupTaskScope;
                    var selectedTasks = taskScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(f => f != CatchupTask.Fingerprint.ToString())
                        .ToList();
                    options.GeneralOptions.CatchupTaskScope = string.Join(",", selectedTasks);
                }

                var isSimpleTokenizer = string.Equals(EnhanceChineseSearch.CurrentTokenizerName, "simple",
                    StringComparison.Ordinal);
                options.ModOptions.EnhanceChineseSearchRestore =
                    !options.ModOptions.EnhanceChineseSearch && isSimpleTokenizer;

                options.NetworkOptions.ProxyServerUrl =
                    !string.IsNullOrWhiteSpace(options.NetworkOptions.ProxyServerUrl)
                        ? options.NetworkOptions.ProxyServerUrl.Trim().TrimEnd('/')
                        : options.NetworkOptions.ProxyServerUrl?.Trim();

                if (!suppress)
                {
                    if (options.NetworkOptions.EnableProxyServer &&
                        !string.IsNullOrWhiteSpace(options.NetworkOptions.ProxyServerUrl))
                    {
                        if (TryParseProxyUrl(options.NetworkOptions.ProxyServerUrl, out var schema, out var host, out var port,
                                out var username, out var password) &&
                            CheckProxyReachability(schema, host, port, username, password) is (true, var httpPing))
                        {
                            options.NetworkOptions.ProxyServerStatus.Status = ItemStatus.Succeeded;
                            options.NetworkOptions.ProxyServerStatus.Caption = Resources.ProxyServer_Available;
                            options.NetworkOptions.ProxyServerStatus.StatusText = $"{httpPing} ms";
                        }
                        else
                        {
                            options.NetworkOptions.ProxyServerStatus.Status = ItemStatus.Unavailable;
                            options.NetworkOptions.ProxyServerStatus.Caption = Resources.ProxyServer_Unavailable;
                            options.NetworkOptions.ProxyServerStatus.StatusText = "N/A";
                        }

                        options.NetworkOptions.ShowProxyServerStatus = true;
                    }
                    else
                    {
                        options.NetworkOptions.ProxyServerStatus.StatusText = string.Empty;
                        options.NetworkOptions.ShowProxyServerStatus = false;
                    }
                }
                
                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(PluginOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (changedProperties.Contains(nameof(PluginOptions.GeneralOptions.CatchupMode)))
                {
                    if (options.GeneralOptions.CatchupMode)
                    {
                        QueueManager.Initialize();
                    }
                    else
                    {
                        QueueManager.Dispose();
                    }
                }

                if (changedProperties.Contains(nameof(PluginOptions.GeneralOptions.CatchupMode)) ||
                    changedProperties.Contains(nameof(PluginOptions.GeneralOptions.CatchupTaskScope)))
                {
                    if (options.GeneralOptions.CatchupMode) UpdateCatchupScope(options.GeneralOptions.CatchupTaskScope);
                }

                if (changedProperties.Contains(nameof(PluginOptions.GeneralOptions.MaxConcurrentCount)))
                {
                    var maxConcurrentCount = options.GeneralOptions.MaxConcurrentCount;

                    QueueManager.UpdateMasterSemaphore(maxConcurrentCount);

                    if (Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture)
                        EnableImageCapture.UpdateResourcePool(maxConcurrentCount);

                    Plugin.FingerprintApi.PatchTimeout(maxConcurrentCount);
                }

                if (changedProperties.Contains(nameof(PluginOptions.GeneralOptions.Tier2MaxConcurrentCount)))
                {
                    QueueManager.UpdateTier2Semaphore(options.GeneralOptions.Tier2MaxConcurrentCount);
                }

                if (changedProperties.Contains(nameof(PluginOptions.ModOptions.EnhanceChineseSearch)) &&
                    ((!options.ModOptions.EnhanceChineseSearch && isSimpleTokenizer) ||
                     (options.ModOptions.EnhanceChineseSearch && !isSimpleTokenizer)))
                {
                    Plugin.Instance.ApplicationHost.NotifyPendingRestart();
                }

                if (changedProperties.Contains(nameof(PluginOptions.ModOptions.SearchScope)) ||
                    changedProperties.Contains(nameof(PluginOptions.ModOptions.EnhanceChineseSearch)))
                {
                    if (options.ModOptions.EnhanceChineseSearch)
                        UpdateSearchScope(options.ModOptions.SearchScope);
                }

                if (changedProperties.Contains(nameof(PluginOptions.NetworkOptions.EnableProxyServer)))
                {
                    if (options.NetworkOptions.EnableProxyServer)
                    {
                        PatchManager.EnableProxyServer.Patch();
                    }
                    else
                    {
                        PatchManager.EnableProxyServer.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(PluginOptions.NetworkOptions.ProxyServerUrl)) ||
                    changedProperties.Contains(nameof(PluginOptions.NetworkOptions.EnableProxyServer)))
                {
                    if (options.NetworkOptions.EnableProxyServer &&
                        options.NetworkOptions.ProxyServerStatus.Status == ItemStatus.Succeeded)
                    {
                        Plugin.Instance.ApplicationHost.NotifyPendingRestart();
                    }
                    else if (!options.NetworkOptions.EnableProxyServer)
                    {
                        Plugin.Instance.ApplicationHost.NotifyPendingRestart();
                    }
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is PluginOptions options)
            {
                var suppress = _currentSuppressOnOptionsSaved;

                if (!suppress)
                {
                    _logger.Info("CatchupMode is set to {0}", options.GeneralOptions.CatchupMode);
                    var catchupTaskScope = GetSelectedCatchupTaskDescription();
                    _logger.Info("CatchupTaskScope is set to {0}",
                        string.IsNullOrEmpty(catchupTaskScope) ? "EMPTY" : catchupTaskScope);

                    _logger.Info("Master MaxConcurrentCount is set to {0}", options.GeneralOptions.MaxConcurrentCount);
                    _logger.Info("CooldownDurationSeconds is set to {0}", options.GeneralOptions.CooldownDurationSeconds);
                    _logger.Info("Tier2 MaxConcurrentCount is set to {0}", options.GeneralOptions.Tier2MaxConcurrentCount);
                    
                    _logger.Info("CatchupMode is set to {0}", options.GeneralOptions.CatchupMode);

                    _logger.Info("EnhanceChineseSearch is set to {0}", options.ModOptions.EnhanceChineseSearch);
                    var searchScope = string.Join(", ",
                        options.ModOptions.SearchScope
                            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s =>
                                Enum.TryParse(s.Trim(), true, out ModOptions.SearchItemType type)
                                    ? type.GetDescription()
                                    : null)
                            .Where(d => d != null) ?? Enumerable.Empty<string>());
                    _logger.Info("EnhanceChineseSearch - SearchScope is set to {0}",
                        string.IsNullOrEmpty(searchScope) ? "ALL" : searchScope);
                    _logger.Info("ExcludeOriginalTitleFromSearch is set to {0}", options.ModOptions.ExcludeOriginalTitleFromSearch);

                    _logger.Info("EnableProxyServer is set to {0}", options.NetworkOptions.EnableProxyServer);
                    _logger.Info("ProxyServerUrl is set to {0}",
                        !string.IsNullOrEmpty(options.NetworkOptions.ProxyServerUrl)
                            ? options.NetworkOptions.ProxyServerUrl
                            : "EMPTY");
                }

                if (suppress) _currentSuppressOnOptionsSaved = false;
            }
        }
    }
}
