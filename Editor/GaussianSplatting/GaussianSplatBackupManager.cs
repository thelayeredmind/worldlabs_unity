// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Manages backups of GaussianSplatAsset .bytes files before Commit to Disk.
    // Backup root is configured in Edit > Project Settings > Gaussian Splat (versioned, team-shared).
    // Layout: <backupRoot>/<assetName>/<yyyy-MM-dd_HHmmss>/{pos,oth,shs,col}.bytes + manifest.txt
    static class GaussianSplatBackupManager
    {
        static string BackupRoot => GaussianSplatProjectSettings.instance.backupRoot;

        // Called by GaussianSplatCommitter before any file writes.
        public static void BackupFiles(string assetName, string pathPos, string pathOther, string pathSH, string pathColor, string pathAsset)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string backupDir = Path.Combine(BackupRoot, assetName, timestamp);
            Directory.CreateDirectory(backupDir);

            CopyIfExists(pathPos,   Path.Combine(backupDir, Path.GetFileName(pathPos)));
            CopyIfExists(pathOther, Path.Combine(backupDir, Path.GetFileName(pathOther)));
            CopyIfExists(pathSH,    Path.Combine(backupDir, Path.GetFileName(pathSH)));
            CopyIfExists(pathColor, Path.Combine(backupDir, Path.GetFileName(pathColor)));
            CopyIfExists(pathAsset, Path.Combine(backupDir, Path.GetFileName(pathAsset)));

            string manifest = $"asset={assetName}\npos={Path.GetFileName(pathPos)}\nother={Path.GetFileName(pathOther)}\nsh={Path.GetFileName(pathSH)}\ncolor={Path.GetFileName(pathColor)}\nassetFile={Path.GetFileName(pathAsset)}\n";
            File.WriteAllText(Path.Combine(backupDir, "manifest.txt"), manifest);
        }

        static void CopyIfExists(string src, string dst)
        {
            if (!string.IsNullOrEmpty(src) && File.Exists(src))
                File.Copy(src, dst, overwrite: true);
        }

        // ──────────────────────────────────────────────────────────────
        // Menu items
        // ──────────────────────────────────────────────────────────────

        [MenuItem("Tools/Gaussian Splats/Restore Backup...")]
        static void RestoreBackup()
        {
            string root = BackupRoot;
            if (!Directory.Exists(root))
            {
                EditorUtility.DisplayDialog("No Backups", $"Backup folder not found:\n{root}\n\nConfigure it in Edit > Project Settings > Gaussian Splat.", "OK");
                return;
            }

            var manifests = Directory.GetFiles(root, "manifest.txt", SearchOption.AllDirectories);
            if (manifests.Length == 0)
            {
                EditorUtility.DisplayDialog("No Backups", "No backups found in:\n" + root, "OK");
                return;
            }

            var entries = manifests
                .Select(m => {
                    var parts = m.Replace('\\', '/').Split('/');
                    string ts   = parts.Length >= 2 ? parts[^2] : "?";
                    string name = parts.Length >= 3 ? parts[^3] : "?";
                    return (display: $"{name}  /  {ts}", dir: Path.GetDirectoryName(m), assetName: name);
                })
                .OrderByDescending(e => e.display)
                .ToArray();

            // For more than 2 entries always use the picker window for clarity
            if (entries.Length > 2)
            {
                RestorePickerWindow.Show(entries.Select(e => (e.display, e.dir, e.assetName)).ToArray());
                return;
            }

            string[] labels = entries.Select(e => e.display).ToArray();
            int chosen = EditorUtility.DisplayDialogComplex(
                "Restore Gaussian Splat Backup",
                "Select a backup to restore (this overwrites the current .bytes files):",
                entries.Length > 0 ? labels[0] : "—",
                "Cancel",
                entries.Length > 1 ? labels[1] : "");

            string restoreDir = chosen switch {
                0 => entries[0].dir,
                2 when entries.Length > 1 => entries[1].dir,
                _ => null
            };
            if (restoreDir != null)
                DoRestore(restoreDir);
        }

        internal static void DoRestore(string backupDir)
        {
            if (!Directory.Exists(backupDir))
            {
                EditorUtility.DisplayDialog("Error", $"Backup directory not found:\n{backupDir}", "OK");
                return;
            }

            var bytesFiles = Directory.GetFiles(backupDir, "*.bytes");
            var assetFiles = Directory.GetFiles(backupDir, "*.asset");
            var allFiles   = bytesFiles.Concat(assetFiles).ToArray();

            if (bytesFiles.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "No .bytes files found in backup.", "OK");
                return;
            }

            // Disable affected renderers first so they drop GPU buffers and release file references,
            // then release Unity's cached handles, then overwrite.
            var affectedRenderers = FindAffectedRenderers(bytesFiles);
            foreach (var gs in affectedRenderers)
                gs.enabled = false;

            AssetDatabase.ReleaseCachedFileHandles();

            bool anyRestored = false;
            foreach (var src in allFiles)
            {
                string fname = Path.GetFileName(src);
                string[] guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(fname));
                foreach (var guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileName(assetPath).Equals(fname, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(src, assetPath, overwrite: true);
                        anyRestored = true;
                        Debug.Log($"[GaussianSplat] Restored: {assetPath}");
                        break;
                    }
                }
            }

            if (!anyRestored)
            {
                // Re-enable before returning
                foreach (var gs in affectedRenderers)
                    gs.enabled = true;
                EditorUtility.DisplayDialog("Restore",
                    "Could not match backup files to project assets. Files may have been moved or renamed.", "OK");
                return;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

            foreach (var gs in affectedRenderers)
                gs.enabled = true;

            EditorUtility.DisplayDialog("Restore Complete", $"Restored {bytesFiles.Length} file(s) from:\n{backupDir}", "OK");
        }

        static GaussianSplatRenderer[] FindAffectedRenderers(string[] restoredFiles)
        {
            var renderers = UnityEngine.Object.FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
            return System.Array.FindAll(renderers, gs => {
                if (gs.asset == null || gs.asset.LayerData == null) return false;
                foreach (var layer in gs.asset.LayerData)
                {
                    string posPath = AssetDatabase.GetAssetPath(layer.m_PosData);
                    if (Array.Exists(restoredFiles, f => Path.GetFileName(f) == Path.GetFileName(posPath)))
                        return true;
                }
                return false;
            });
        }

    }

    // EditorWindow for picking from more than 2 backups
    class RestorePickerWindow : EditorWindow
    {
        (string display, string dir, string assetName)[] m_Entries;
        Vector2 m_Scroll;

        public static void Show((string display, string dir, string assetName)[] entries)
        {
            var win = GetWindow<RestorePickerWindow>("Restore Backup");
            win.m_Entries = entries;
            win.minSize = new Vector2(480, 300);
            win.ShowUtility();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Select a backup to restore:", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("This will overwrite the current .bytes files on disk.", MessageType.Warning);
            EditorGUILayout.Space();

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var entry in m_Entries)
            {
                if (GUILayout.Button(entry.display))
                {
                    Close();
                    GaussianSplatBackupManager.DoRestore(entry.dir);
                    return;
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            if (GUILayout.Button("Cancel"))
                Close();
        }
    }
}
