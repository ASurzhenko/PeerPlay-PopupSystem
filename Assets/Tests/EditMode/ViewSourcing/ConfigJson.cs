using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PeerPlay.Popups.ViewSourcing.Tests
{
    /// <summary>
    /// Config payloads for the suite. A null argument OMITS the field, which is the only way to test the
    /// absent-field rules — the whole reason every per-entry field is a string on the wire.
    /// </summary>
    internal static class ConfigJson
    {
        internal const string GoldenRelativePath = "Tests/EditMode/Fixtures/popup-config.golden.json";

        internal static string Golden()
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, GoldenRelativePath));
        }

        internal static string One(string id = "info",
                                   string assetId = "popup_info",
                                   string state = "enabled",
                                   string priority = "Normal",
                                   string sequencing = "Queue",
                                   string modality = "Modal",
                                   string transition = "fade",
                                   string suspend = "Hide",
                                   string imageUrl = "",
                                   string titleKey = "info.title",
                                   string bodyKey = "info.body",
                                   string cooldownSeconds = "0",
                                   string maxPerSession = "0",
                                   int version = 1)
        {
            return Wrap(version, Entry(id, assetId, state, priority, sequencing, modality, transition,
                                       suspend, imageUrl, titleKey, bodyKey, cooldownSeconds, maxPerSession));
        }

        internal static string Two(string firstId, string secondId)
        {
            string first = Entry(firstId, "popup_a", "enabled", "Normal", "Queue", "Modal", "fade", "Hide",
                                 "", "a.title", "a.body", "0", "0");
            string second = Entry(secondId, "popup_b", "enabled", "Normal", "Queue", "Modal", "fade", "Hide",
                                  "", "b.title", "b.body", "0", "0");
            return Wrap(1, first + "," + second);
        }

        internal static string Entry(string id, string assetId, string state, string priority, string sequencing,
                                     string modality, string transition, string suspend, string imageUrl,
                                     string titleKey, string bodyKey, string cooldownSeconds, string maxPerSession)
        {
            List<string> fields = new List<string>(13);

            Append(fields, "id", id);
            Append(fields, "assetId", assetId);
            Append(fields, "state", state);
            Append(fields, "priority", priority);
            Append(fields, "sequencing", sequencing);
            Append(fields, "modality", modality);
            Append(fields, "transition", transition);
            Append(fields, "suspend", suspend);
            Append(fields, "imageUrl", imageUrl);
            Append(fields, "titleKey", titleKey);
            Append(fields, "bodyKey", bodyKey);
            Append(fields, "cooldownSeconds", cooldownSeconds);
            Append(fields, "maxPerSession", maxPerSession);

            return "{" + string.Join(",", fields) + "}";
        }

        internal static string Wrap(int version, string entriesJson)
        {
            return "{\"version\":" + version + ",\"popups\":[" + entriesJson + "]}";
        }

        /// <summary>Under the cap in characters and over it in bytes — the rule reads bytes for this reason.</summary>
        internal static string OversizedByBytes()
        {
            StringBuilder padding = new StringBuilder(100000);
            for (int i = 0; i < 100000; i++)
            {
                padding.Append('€');   // 3 bytes in UTF-8
            }

            return One(titleKey: padding.ToString());
        }

        private static void Append(List<string> fields, string name, string value)
        {
            if (value == null)
            {
                return;
            }

            fields.Add("\"" + name + "\":\"" + value + "\"");
        }
    }
}
