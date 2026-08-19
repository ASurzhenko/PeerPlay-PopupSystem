using System;
using System.IO;
using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The last known good config, written atomically. The path is injected rather than read from
    /// Application.persistentDataPath so the tests use a temp directory instead of fighting each other —
    /// and the running Editor — over one real file.
    /// </summary>
    public sealed class PopupConfigCache
    {
        private readonly string _path;
        private readonly string _tmpPath;

        public PopupConfigCache(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _tmpPath = path + ".tmp";
        }

        public string Path => _path;

        public bool TryRead(out string rawJson)
        {
            rawJson = null;

            try
            {
                if (!File.Exists(_path))
                {
                    return false;
                }

                rawJson = File.ReadAllText(_path);
                return !string.IsNullOrEmpty(rawJson);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{nameof(PopupConfigCache)}.{nameof(TryRead)} [Config] unreadable: {e.Message}");
                return false;
            }
        }

        public void Write(string rawJson)
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_tmpPath, rawJson);

                // The branch matters: File.Replace throws FileNotFoundException when the destination does
                // not exist, which is every fresh install.
                if (File.Exists(_path))
                {
                    File.Replace(_tmpPath, _path, null);
                }
                else
                {
                    File.Move(_tmpPath, _path);
                }
            }
            catch (Exception e)
            {
                // A cache we could not write is a slower next boot, never a broken one — the built-in
                // default still validates. Loud, because a permanently unwritable cache is worth knowing.
                Debug.LogError($"{nameof(PopupConfigCache)}.{nameof(Write)} [Config] failed: {e.Message}");
            }
        }

        public void Delete()
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{nameof(PopupConfigCache)}.{nameof(Delete)} [Config] failed: {e.Message}");
            }
        }
    }
}
