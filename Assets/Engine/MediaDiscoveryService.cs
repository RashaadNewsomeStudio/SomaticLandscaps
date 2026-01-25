using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using SomaticLandscapes.Async;

public class MediaDiscoveryService
{
    private readonly ArtworkController _ctrl;
    private readonly AsyncAssetManager _assetManager;

    public MediaDiscoveryService(ArtworkController ctrl, AsyncAssetManager assetManager)
    {
        _ctrl = ctrl;
        _assetManager = assetManager;
    }

    public void AutoPopulateIfNeeded()
    {
        // Simple sync check to populate default lists if empty
        AutoPopulateFromStreamingAssets();
    }

    public async Task AutoDiscoverVideosAsync(CancellationToken ct)
    {
        var idleSharedList = new List<ArtworkController.StreamingAssetRef>(_ctrl.idleShared ?? new ArtworkController.StreamingAssetRef[0]);
        var activeListList = new List<ArtworkController.StreamingAssetRef>(_ctrl.activeList ?? new ArtworkController.StreamingAssetRef[0]);

        var existingIdle = new HashSet<string>(idleSharedList.Select(r => Path.GetFileName(ArtworkController.GetRel(r) ?? "")), StringComparer.OrdinalIgnoreCase);
        var existingAct  = new HashSet<string>(activeListList.Select(r => Path.GetFileName(ArtworkController.GetRel(r) ?? "")), StringComparer.OrdinalIgnoreCase);

        // Ambient Discovery
        if (!string.IsNullOrEmpty(_ctrl._externalAmbientPath) && Directory.Exists(_ctrl._externalAmbientPath))
            await TryAppendMovsFromFolderAsync(_ctrl._externalAmbientPath, idleSharedList, existingIdle, null, ct);
        else
        {
            var saAmbient = Path.Combine(Application.streamingAssetsPath, "Ambient");
            if (Directory.Exists(saAmbient))
                await TryAppendMovsFromFolderAsync(saAmbient, idleSharedList, existingIdle, "Ambient", ct);
        }

        // Active Discovery
        if (!string.IsNullOrEmpty(_ctrl._externalActivePath) && Directory.Exists(_ctrl._externalActivePath))
            await TryAppendMovsFromFolderAsync(_ctrl._externalActivePath, activeListList, existingAct, null, ct);
        else
        {
            var saActive = Path.Combine(Application.streamingAssetsPath, "Active");
            if (Directory.Exists(saActive))
                await TryAppendMovsFromFolderAsync(saActive, activeListList, existingAct, "Active", ct);
        }

        _ctrl.idleShared = idleSharedList.ToArray();
        _ctrl.activeList = activeListList.ToArray();

        ControllerMain.LogStep($"Auto-discovered media (Async): Idle={_ctrl.idleShared.Length}, Active={_ctrl.activeList.Length}");
    }

    private void AutoPopulateFromStreamingAssets()
    {
        bool needAmbient = !ArtworkController.HasAnyValid(_ctrl.idleAList) && !ArtworkController.HasAnyValid(_ctrl.idleBList) && !ArtworkController.HasAnyValid(_ctrl.idleShared);
        bool needActive  = !ArtworkController.HasAnyValid(_ctrl.activeList);
        if (!needAmbient && !needActive) return;

        string saRoot = Application.streamingAssetsPath.Replace('\\', '/');

        if (needAmbient)
        {
            string ambientDir = Path.Combine(saRoot, "Ambient").Replace('\\', '/');
            if (Directory.Exists(ambientDir))
            {
                var files = Directory.GetFiles(ambientDir, "*.mov");
                var temp = new List<ArtworkController.StreamingAssetRef>();
                foreach (var f in files)
                    temp.Add(new ArtworkController.StreamingAssetRef { relativePath = "Ambient/" + Path.GetFileName(f) });
                if (temp.Count > 0) _ctrl.idleShared = temp.ToArray();
            }
        }

        if (needActive)
        {
            string activeDir = Path.Combine(saRoot, "Active").Replace('\\', '/');
            if (Directory.Exists(activeDir))
            {
                var files = Directory.GetFiles(activeDir, "*.mov");
                var temp = new List<ArtworkController.StreamingAssetRef>();
                foreach (var f in files)
                    temp.Add(new ArtworkController.StreamingAssetRef { relativePath = "Active/" + Path.GetFileName(f) });
                if (temp.Count > 0) _ctrl.activeList = temp.ToArray();
            }
        }
    }

    private async Task TryAppendMovsFromFolderAsync(
        string folderAbs, 
        List<ArtworkController.StreamingAssetRef> target, 
        HashSet<string> existingNames, 
        string relativeBaseForSA, 
        CancellationToken ct)
    {
        string[] files;
        try
        {
            // Use AsyncAssetManager for threaded discovery
            var f1 = await _assetManager.DiscoverVideosAsync(folderAbs, "*.mov", ct);
            var f2 = await _assetManager.DiscoverVideosAsync(folderAbs, "*.MOV", ct);
            files = f1.Concat(f2).Distinct().ToArray();
        }
        catch
        {
            // Fallback
                files = await _assetManager.DiscoverVideosAsync(folderAbs, "*.mov", ct);
        }

        foreach (var abs in files)
        {
            var name = Path.GetFileName(abs);
            if (string.IsNullOrEmpty(name) || existingNames.Contains(name)) continue;

            var r = new ArtworkController.StreamingAssetRef
            {
                relativePath = string.IsNullOrEmpty(relativeBaseForSA)
                    ? name
                    : relativeBaseForSA.Replace('\\', '/').TrimEnd('/') + "/" + name
            };

            target.Add(r);
            existingNames.Add(name);
        }
    }

    public string ResolveLoadPath(string rel, bool isActive)
    {
        string filename = Path.GetFileName(rel);
        string baseFolder = isActive ? _ctrl._externalActivePath : _ctrl._externalAmbientPath;
        if (!string.IsNullOrEmpty(baseFolder) && !string.IsNullOrEmpty(filename))
        {
                string abs = Path.Combine(baseFolder, filename);
                if (File.Exists(abs)) return abs;
        }
        return Path.Combine(Application.streamingAssetsPath, rel);
    }
}
