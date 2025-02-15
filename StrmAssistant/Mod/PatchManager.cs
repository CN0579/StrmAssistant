using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace StrmAssistant.Mod
{
    public static class PatchManager
    {
        public static Harmony HarmonyMod;
        public static readonly List<PatchTracker> PatchTrackerList = new List<PatchTracker>();

        public static EnableImageCapture EnableImageCapture;
        public static EnhanceChineseSearch EnhanceChineseSearch;
        public static MergeMultiVersion MergeMultiVersion;
        public static ExclusiveExtract ExclusiveExtract;
        public static ChineseMovieDb ChineseMovieDb;
        public static ChineseTvdb ChineseTvdb;
        public static EnhanceMovieDbPerson EnhanceMovieDbPerson;
        public static AltMovieDbConfig AltMovieDbConfig;
        public static EnableProxyServer EnableProxyServer;
        public static PreferOriginalPoster PreferOriginalPoster;
        public static UnlockIntroSkip UnlockIntroSkip;
        public static PinyinSortName PinyinSortName;
        public static EnhanceNfoMetadata EnhanceNfoMetadata;
        public static HidePersonNoImage HidePersonNoImage;
        public static EnforceLibraryOrder EnforceLibraryOrder;
        public static BeautifyMissingMetadata BeautifyMissingMetadata;
        public static EnhanceMissingEpisodes EnhanceMissingEpisodes;
        public static ChapterChangeTracker ChapterChangeTracker;
        public static MovieDbEpisodeGroup MovieDbEpisodeGroup;
        public static NoBoxsetsAutoCreation NoBoxsetsAutoCreation;
        public static EnhanceNotificationSystem EnhanceNotificationSystem;
        public static EnableDeepDelete EnableDeepDelete;

        private static readonly ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod> HarmonyMethodCache 
            = new ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod>();
        private static readonly ConcurrentDictionary<Tuple<Type, string>, MethodInfo> MethodInfoCache 
            = new ConcurrentDictionary<Tuple<Type, string>, MethodInfo>();

        public static void Initialize()
        {
            try
            {
                HarmonyMod = new Harmony("emby.mod");
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("Harmony Init Failed");
                Plugin.Instance.Logger.Debug(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }

            EnableImageCapture = new EnableImageCapture();
            EnhanceChineseSearch = new EnhanceChineseSearch();
            MergeMultiVersion = new MergeMultiVersion();
            ExclusiveExtract = new ExclusiveExtract();
            ChineseMovieDb = new ChineseMovieDb();
            ChineseTvdb = new ChineseTvdb();
            EnhanceMovieDbPerson = new EnhanceMovieDbPerson();
            AltMovieDbConfig = new AltMovieDbConfig();
            EnableProxyServer = new EnableProxyServer();
            PreferOriginalPoster = new PreferOriginalPoster();
            UnlockIntroSkip = new UnlockIntroSkip();
            PinyinSortName = new PinyinSortName();
            EnhanceNfoMetadata = new EnhanceNfoMetadata();
            HidePersonNoImage = new HidePersonNoImage();
            EnforceLibraryOrder = new EnforceLibraryOrder();
            BeautifyMissingMetadata = new BeautifyMissingMetadata();
            EnhanceMissingEpisodes = new EnhanceMissingEpisodes();
            ChapterChangeTracker = new ChapterChangeTracker();
            MovieDbEpisodeGroup = new MovieDbEpisodeGroup();
            NoBoxsetsAutoCreation = new NoBoxsetsAutoCreation();
            EnhanceNotificationSystem = new EnhanceNotificationSystem();
            EnableDeepDelete = new EnableDeepDelete();
        }

        public static bool IsPatched(MethodBase methodInfo, Type type)
        {
            var patchedMethods = Harmony.GetAllPatchedMethods();
            if (!patchedMethods.Contains(methodInfo)) return false;
            var patchInfo = Harmony.GetPatchInfo(methodInfo);

            return patchInfo.Prefixes.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Postfixes.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Transpilers.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type);
        }

        public static bool WasCalledByMethod(Assembly assembly, string callingMethodName)
        {
            var stackFrames = new StackTrace(1, false).GetFrames();

            return stackFrames.Any(f =>
            {
                var method = f?.GetMethod();
                return method?.DeclaringType?.Assembly == assembly && method?.Name == callingMethodName;
            });
        }

        public static bool? IsModSuccess()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;

            return PatchTrackerList.Where(p => p.IsSupported)
                .All(p => p.FallbackPatchApproach == p.DefaultPatchApproach);
        }

        public static bool PatchUnpatch(PatchTracker tracker, bool apply, MethodBase targetMethod, string prefix = null,
            string postfix = null, string transpiler = null, string finalizer = null, bool suppress = false)
        {
            if (tracker.FallbackPatchApproach != PatchApproach.Harmony) return false;

            if (targetMethod is null)
            {
                Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} Init Failed");
                tracker.FallbackPatchApproach = PatchApproach.None;
                return false;
            }

            var action = apply ? "Patch" : "Unpatch";

            try
            {
                if (apply && !IsPatched(targetMethod, tracker.PatchType))
                {
                    var prefixMethod = GetHarmonyMethod(tracker.PatchType, prefix);
                    var postfixMethod = GetHarmonyMethod(tracker.PatchType, postfix);
                    var transpilerMethod = GetHarmonyMethod(tracker.PatchType, transpiler);
                    var finalizerMethod = GetHarmonyMethod(tracker.PatchType, finalizer);

                    HarmonyMod.Patch(targetMethod, prefixMethod, postfixMethod, transpilerMethod, finalizerMethod);
                }
                else if (!apply && IsPatched(targetMethod, tracker.PatchType))
                {
                    var prefixMethod = GetMethodInfo(tracker.PatchType, prefix);
                    var postfixMethod = GetMethodInfo(tracker.PatchType, postfix);
                    var transpilerMethod = GetMethodInfo(tracker.PatchType, transpiler);
                    var finalizerMethod = GetMethodInfo(tracker.PatchType, finalizer);

                    if (prefixMethod != null) HarmonyMod.Unpatch(targetMethod, prefixMethod);
                    if (postfixMethod != null) HarmonyMod.Unpatch(targetMethod, postfixMethod);
                    if (transpilerMethod != null) HarmonyMod.Unpatch(targetMethod, transpilerMethod);
                    if (finalizerMethod != null) HarmonyMod.Unpatch(targetMethod, finalizerMethod);
                }

                if (!suppress)
                {
                    Plugin.Instance.Logger.Debug(
                        $"{action} {(targetMethod.DeclaringType != null ? targetMethod.DeclaringType.Name + "." : string.Empty)}{targetMethod.Name} for {tracker.PatchType.Name} Success");
                }

                return true;
            }
            catch (Exception he)
            {
                Plugin.Instance.Logger.Debug($"{action} {targetMethod.Name} for {tracker.PatchType.Name} Failed");
                Plugin.Instance.Logger.Debug(he.Message);
                Plugin.Instance.Logger.Debug(he.StackTrace);
                tracker.FallbackPatchApproach = PatchApproach.Reflection;
            }

            return false;
        }

        public static bool PatchUnpatch(PatchTracker tracker, bool apply, MethodBase targetMethod, ref int usageCount,
            string prefix = null, string postfix = null, string transpiler = null, string finalizer = null,
            bool suppress = false)
        {
            if (apply)
            {
                if (usageCount == 0)
                {
                    if (PatchUnpatch(tracker, true, targetMethod, prefix, postfix, transpiler, finalizer, suppress))
                    {
                        usageCount++;
                        return true;
                    }

                    return false;
                }

                usageCount++;
            }
            else
            {
                if (usageCount <= 0)
                    throw new InvalidOperationException();

                usageCount--;

                if (usageCount == 0)
                {
                    return PatchUnpatch(tracker, false, targetMethod, prefix, postfix, transpiler, finalizer, suppress);
                }
            }

            return true;
        }

        private static HarmonyMethod GetHarmonyMethod(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            return HarmonyMethodCache.GetOrAdd(Tuple.Create(patchType, patchMethod), tuple =>
            {
                var methodInfo = GetMethodInfo(tuple.Item1, tuple.Item2);
                return methodInfo != null ? new HarmonyMethod(methodInfo) : null;
            });
        }

        private static MethodInfo GetMethodInfo(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            return MethodInfoCache.GetOrAdd(Tuple.Create(patchType, patchMethod),
                tuple => AccessTools.Method(tuple.Item1, tuple.Item2));
        }
    }
}
