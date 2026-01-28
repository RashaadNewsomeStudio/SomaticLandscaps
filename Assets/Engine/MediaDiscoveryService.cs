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
        // Ambient: Check A, B, and Shared
        bool hasAmbient = ArtworkController.HasAnyValid(_ctrl.idleAList) || 
                          ArtworkController.HasAnyValid(_ctrl.idleBList) || 
                          ArtworkController.HasAnyValid(_ctrl.idleShared);
        
        if (!hasAmbient) AutoPopulateList(ref _ctrl.idleShared, "Ambient");
        
        // Active
        if (!ArtworkController.HasAnyValid(_ctrl.activeList)) 
            AutoPopulateList(ref _ctrl.activeList, "Active");
    }

    private void AutoPopulateList(ref ArtworkController.StreamingAssetRef[] list, string subfolder)
    {
        string dir = Path.Combine(Application.streamingAssetsPath, subfolder).Replace('\\', '/');
        if (!Directory.Exists(dir)) return;

        var temp = new List<ArtworkController.StreamingAssetRef>();
        foreach (var f in Directory.GetFiles(dir, "*.mov"))
            temp.Add(new ArtworkController.StreamingAssetRef { relativePath = $"{subfolder}/{Path.GetFileName(f)}" });

        if (temp.Count > 0)
        {
            list = temp.ToArray();
            ControllerMain.LogStep($"[MediaDiscovery] Auto-populated {subfolder} (Sync): {temp.Count} files");
        }
    }

    public async Task AutoDiscoverVideosAsync(CancellationToken ct)
    {
        var idleList = new List<ArtworkController.StreamingAssetRef>(_ctrl.idleShared ?? new ArtworkController.StreamingAssetRef[0]);
        var activeList = new List<ArtworkController.StreamingAssetRef>(_ctrl.activeList ?? new ArtworkController.StreamingAssetRef[0]);

        await DiscoverAndAppend(idleList, _ctrl._externalAmbientPath, "Ambient", ct);
        await DiscoverAndAppend(activeList, _ctrl._externalActivePath, "Active", ct);

        _ctrl.idleShared = idleList.ToArray();
        _ctrl.activeList = activeList.ToArray();

        ControllerMain.LogStep($"Auto-discovered media (Async): Idle={_ctrl.idleShared.Length}, Active={_ctrl.activeList.Length}");
    }

    private async Task DiscoverAndAppend(List<ArtworkController.StreamingAssetRef> target, string externalPath, string saSubfolder, CancellationToken ct)
    {
        var existing = new HashSet<string>(target.Select(r => Path.GetFileName(ArtworkController.GetRel(r) ?? "")), StringComparer.OrdinalIgnoreCase);
        
        if (!string.IsNullOrEmpty(externalPath) && Directory.Exists(externalPath))
        {
             await TryAppendMovsFromFolderAsync(externalPath, target, existing, null, ct);
        }
        else
        {
            var saDir = Path.Combine(Application.streamingAssetsPath, saSubfolder);
            if (Directory.Exists(saDir))
                 await TryAppendMovsFromFolderAsync(saDir, target, existing, saSubfolder, ct);
        }
    }

    private async Task TryAppendMovsFromFolderAsync(string folder, List<ArtworkController.StreamingAssetRef> target, HashSet<string> existing, string relBase, CancellationToken ct)
    {
        try
        {
            var f1 = await _assetManager.DiscoverVideosAsync(folder, "*.mov", ct);
            var f2 = await _assetManager.DiscoverVideosAsync(folder, "*.MOV", ct);
            
            foreach (var abs in f1.Concat(f2).Distinct())
            {
                var name = Path.GetFileName(abs);
                if (string.IsNullOrEmpty(name) || existing.Contains(name)) continue;

                target.Add(new ArtworkController.StreamingAssetRef {
                    relativePath = string.IsNullOrEmpty(relBase) ? name : $"{relBase}/{name}"
                });
                existing.Add(name);
            }
        }
        catch (Exception ex)
        {
            ControllerMain.LogWarn($"Discovery failed in {folder}: {ex.Message}");
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
