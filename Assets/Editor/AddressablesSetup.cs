using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace PeerPlay.Popups.EditorTools
{
    /// <summary>
    /// The two Addressables groups the local/remote split is made of. There is one code path for both —
    /// the difference is which group an entry sits in, and that is decided here rather than in code.
    /// </summary>
    internal static class AddressablesSetup
    {
        private const string LocalGroup = "Popups_Local";
        private const string RemoteGroup = "Popups_Remote";
        private const string ReportPath = "Tools/addressables-setup-report.json";

        private const string RemoteLoadPath =
            "https://d2eupgfrfppc7x.cloudfront.net/peerplay/[BuildTarget]";

        private const string LocalLoadVariable = "Local.LoadPath";
        private const string LocalBuildVariable = "Local.BuildPath";
        private const string RemoteLoadVariable = "Remote.LoadPath";
        private const string RemoteBuildVariable = "Remote.BuildPath";

        private static readonly (string path, string address)[] LocalEntries =
        {
            ("Assets/Prefabs/Popups/popup_info.prefab", "popup_info"),
            ("Assets/Prefabs/Popups/popup_confirm.prefab", "popup_confirm"),
            ("Assets/Prefabs/Popups/popup_reward.prefab", "popup_reward"),

            // The shared atlas is addressable ON PURPOSE and lives in the LOCAL group. Addressables copies
            // an implicit dependency into every bundle that references it, so an unmarked atlas referenced
            // by both a local prefab and the remote offer would be packed into both — the remote download
            // silently carrying the whole UI kit. Marked, it lives in one bundle and the remote bundle
            // carries a reference. The dupe analyze in this file is what proves it.
            ("Assets/Art/Generated/UIKit.spriteatlasv2", "uikit_atlas"),
            ("Assets/Fonts/Fredoka SDF.asset", "font_fredoka")
        };

        private static readonly (string path, string address)[] RemoteEntries =
        {
            ("Assets/Prefabs/Popups/popup_offer.prefab", "popup_offer"),

            // One entry whose only job is to answer "did the remote catalog actually load". Init succeeds
            // even when it did not, so the question needs an entry that exists only remotely.
            ("Assets/Config/popup_remote_probe.txt", "popup_remote_probe")
        };

        [MenuItem("Tools/PeerPlay/Setup Addressables Groups")]
        internal static void Setup()
        {
            List<string> log = new List<string>();

            try
            {
                AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);

                // The probe asks about a REMOTE catalog, so one has to be published at all.
                settings.BuildRemoteCatalog = true;

                // The profile variables are named with a DOT ("Remote.LoadPath"), and both SetValue and
                // SetVariableByName are no-ops on a name that does not exist — so a typo here leaves the
                // remote group loading from the local path and nothing says a word. The verification at the
                // end of this method is what turns that into a failure.
                string profileId = settings.activeProfileId;
                settings.profileSettings.SetValue(profileId, RemoteLoadVariable, RemoteLoadPath);
                settings.profileSettings.SetValue(profileId, RemoteBuildVariable, "ServerData/[BuildTarget]");
                log.Add($"profile {RemoteLoadVariable}={RemoteLoadPath}");

                AddressableAssetGroup local = EnsureGroup(settings, LocalGroup, false);
                AddressableAssetGroup remote = EnsureGroup(settings, RemoteGroup, true);

                foreach ((string path, string address) in LocalEntries)
                {
                    log.Add(AddEntry(settings, local, path, address));
                }

                foreach ((string path, string address) in RemoteEntries)
                {
                    log.Add(AddEntry(settings, remote, path, address));
                }

                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
                AssetDatabase.SaveAssets();

                Verify(settings, log);

                WriteReport("ok", null, log);
                Debug.Log($"{nameof(AddressablesSetup)}.{nameof(Setup)} done — report at {ReportPath}");
            }
            catch (Exception e)
            {
                WriteReport("failed", $"{e.GetType().Name}: {e.Message}", log);
                Debug.LogException(e);
                throw;
            }
        }

        /// <summary>
        /// Reads the result back off the settings instead of trusting the writes. Both APIs above fail by
        /// doing nothing, and the state they fail into — a remote group loading from the local path — is
        /// invisible until a device cannot find its content.
        /// </summary>
        private static void Verify(AddressableAssetSettings settings, List<string> log)
        {
            string resolved = settings.profileSettings.GetValueByName(settings.activeProfileId, RemoteLoadVariable);

            if (resolved != RemoteLoadPath)
            {
                throw new InvalidOperationException(
                    $"{RemoteLoadVariable} reads back as '{resolved}', not '{RemoteLoadPath}' — the profile " +
                    "variable name is wrong and every remote load would resolve locally");
            }

            AddressableAssetGroup remote = settings.FindGroup(RemoteGroup);
            string remoteLoad = remote.GetSchema<BundledAssetGroupSchema>().LoadPath.GetName(settings);

            if (remoteLoad != RemoteLoadVariable)
            {
                throw new InvalidOperationException(
                    $"{RemoteGroup} loads from '{remoteLoad}', not '{RemoteLoadVariable}' — it is not remote");
            }

            AddressableAssetGroup local = settings.FindGroup(LocalGroup);
            string localLoad = local.GetSchema<BundledAssetGroupSchema>().LoadPath.GetName(settings);

            if (localLoad != LocalLoadVariable)
            {
                throw new InvalidOperationException($"{LocalGroup} loads from '{localLoad}', not '{LocalLoadVariable}'");
            }

            log.Add($"verified: {RemoteGroup} -> {remoteLoad} = {resolved}");
            log.Add($"verified: {LocalGroup} -> {localLoad}");
        }

        private static AddressableAssetGroup EnsureGroup(AddressableAssetSettings settings, string name, bool remote)
        {
            AddressableAssetGroup group = settings.FindGroup(name);

            if (group == null)
            {
                group = settings.CreateGroup(name, false, false, false, null,
                                             typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            }

            BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>()
                                             ?? group.AddSchema<BundledAssetGroupSchema>();

            schema.BuildPath.SetVariableByName(settings, remote ? RemoteBuildVariable : LocalBuildVariable);
            schema.LoadPath.SetVariableByName(settings, remote ? RemoteLoadVariable : LocalLoadVariable);

            // Local packs together — one bundle, fewest requests. Remote packs separately so a single
            // popup can be re-published without re-downloading the rest.
            schema.BundleMode = remote
                ? BundledAssetGroupSchema.BundlePackingMode.PackSeparately
                : BundledAssetGroupSchema.BundlePackingMode.PackTogether;

            // LZ4 on both. LZMA only pays when download size dominates, and it costs a full decompress on
            // first use — the wrong trade for a handful of popups.
            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            schema.IncludeInBuild = true;

            return group;
        }

        private static string AddEntry(AddressableAssetSettings settings, AddressableAssetGroup group,
                                       string path, string address)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);

            if (string.IsNullOrEmpty(guid))
            {
                return $"MISSING {path}";
            }

            AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
            entry.address = address;
            return $"{group.Name}: {address} <- {path}";
        }

        /// <summary>
        /// Duplicate Bundle Dependencies, run and RECORDED. Unrun, the claim that the shared atlas is not
        /// packed into both bundles is unproven — which is why the result goes in the report as a measured
        /// number rather than into the README as a promise.
        /// </summary>
        [MenuItem("Tools/PeerPlay/Analyze Duplicate Bundle Dependencies")]
        internal static void AnalyzeDuplicates()
        {
            List<string> log = new List<string>();

            try
            {
                AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);

                var rule = new UnityEditor.AddressableAssets.Build.AnalyzeRules.CheckBundleDupeDependencies();
                List<UnityEditor.AddressableAssets.Build.AnalyzeRules.AnalyzeRule.AnalyzeResult> results =
                    rule.RefreshAnalysis(settings);

                int duplicates = 0;

                for (int i = 0; i < results.Count; i++)
                {
                    string name = results[i].resultName ?? string.Empty;

                    // The rule reports a "no issues" row too; only rows describing an actual duplication
                    // count, and each one is recorded verbatim so the number can be checked.
                    if (name.IndexOf("No issues", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    duplicates++;
                    log.Add(name);
                }

                log.Insert(0, $"duplicate_rows={duplicates} total_rows={results.Count}");
                WriteReport("ok", null, log, "Tools/addressables-dupe-report.json");
                Debug.Log($"{nameof(AddressablesSetup)}.{nameof(AnalyzeDuplicates)} duplicates={duplicates}");
            }
            catch (Exception e)
            {
                WriteReport("failed", $"{e.GetType().Name}: {e.Message}", log, "Tools/addressables-dupe-report.json");
                Debug.LogException(e);
                throw;
            }
        }

        /// <summary>
        /// The BEFORE half of the measurement. A clean analyze proves nothing on its own — it is the
        /// result you get whether or not the atlas entry is doing any work. This temporarily un-marks the
        /// atlas so it becomes an implicit dependency of both groups, re-runs the rule, and restores it.
        /// If the "before" number is also zero then the atlas entry is not what is preventing duplication,
        /// and the reason the groups are laid out this way needs re-examining.
        /// </summary>
        [MenuItem("Tools/PeerPlay/Analyze Duplicates — Before/After")]
        internal static void AnalyzeBeforeAfter()
        {
            List<string> log = new List<string>();
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            string atlasGuid = AssetDatabase.AssetPathToGUID("Assets/Art/Generated/UIKit.spriteatlasv2");

            try
            {
                log.Add($"after  (atlas addressable):     duplicate_rows={CountDuplicates(settings)}");

                settings.RemoveAssetEntry(atlasGuid, false);
                log.Add($"before (atlas NOT addressable): duplicate_rows={CountDuplicates(settings)}");

                WriteReport("ok", null, log, "Tools/addressables-dupe-report.json");
                Debug.Log($"{nameof(AddressablesSetup)}.{nameof(AnalyzeBeforeAfter)} done");
            }
            catch (Exception e)
            {
                WriteReport("failed", $"{e.GetType().Name}: {e.Message}", log, "Tools/addressables-dupe-report.json");
                Debug.LogException(e);
                throw;
            }
            finally
            {
                // Restored whatever happened above: leaving the atlas un-marked is the very state this
                // measurement exists to argue against.
                AddressableAssetGroup local = EnsureGroup(settings, LocalGroup, false);
                AddressableAssetEntry restored = settings.CreateOrMoveEntry(atlasGuid, local, false, false);
                restored.address = "uikit_atlas";
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
                AssetDatabase.SaveAssets();
            }
        }

        private static int CountDuplicates(AddressableAssetSettings settings)
        {
            var rule = new UnityEditor.AddressableAssets.Build.AnalyzeRules.CheckBundleDupeDependencies();
            var results = rule.RefreshAnalysis(settings);

            int duplicates = 0;

            for (int i = 0; i < results.Count; i++)
            {
                string name = results[i].resultName ?? string.Empty;
                if (name.IndexOf("No issues", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    duplicates++;
                }
            }

            return duplicates;
        }

        private static void WriteReport(string status, string error, List<string> log, string path = ReportPath)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"status\": \"").Append(status).Append("\",\n");
            sb.Append("  \"error\": ").Append(error == null ? "null" : "\"" + error.Replace("\"", "'") + "\"").Append(",\n");
            sb.Append("  \"entries\": [\n");

            for (int i = 0; i < log.Count; i++)
            {
                sb.Append("    \"").Append(log[i].Replace("\\", "/").Replace("\"", "'")).Append('"');
                sb.Append(i == log.Count - 1 ? "\n" : ",\n");
            }

            sb.Append("  ]\n}\n");

            Directory.CreateDirectory("Tools");
            File.WriteAllText(path, sb.ToString());
        }
    }
}
