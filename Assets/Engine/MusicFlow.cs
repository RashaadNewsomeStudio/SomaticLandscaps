using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using SomaticLandscapes.Async;

public class MusicFlow
{
    private readonly ArtworkController _ctrl;
    private readonly AsyncAssetManager _assetManager;

    public MusicFlow(ArtworkController ctrl, AsyncAssetManager assetManager)
    {
        _ctrl = ctrl;
        _assetManager = assetManager;
    }

    public async Task LoadMusicAsync(CancellationToken ct)
    {
        if (!_ctrl.musicSource) return;

        if (_ctrl.autoLoadMusicFromStreaming && !string.IsNullOrEmpty(_ctrl.musicSubfolder))
        {
            string folder = Path.Combine(Application.streamingAssetsPath, _ctrl.musicSubfolder);
            if (Directory.Exists(folder))
            {
                var wavs = Directory.GetFiles(folder, "*.wav");
                var mp3s = Directory.GetFiles(folder, "*.mp3");
                var files = new string[wavs.Length + mp3s.Length];
                wavs.CopyTo(files, 0);
                mp3s.CopyTo(files, wavs.Length);

                if (files.Length > 0)
                {
                    // Pick random
                    string randRef = files[Random.Range(0, files.Length)];
                    ControllerMain.LogStep($"[Music] Loading random track: {Path.GetFileName(randRef)}");

                    string url = "file://" + randRef.Replace('\\', '/');
                    using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.UNKNOWN))
                    {
                        var op = uwr.SendWebRequest();
                        while (!op.isDone && !ct.IsCancellationRequested) await Task.Yield();
                        
                        if (uwr.result == UnityWebRequest.Result.Success)
                        {
                            var clip = DownloadHandlerAudioClip.GetContent(uwr);
                            clip.name = Path.GetFileName(randRef);
                            _ctrl.musicSource.clip = clip;
                        }
                        else
                        {
                            ControllerMain.LogWarn($"[Music] Failed to load {randRef}: {uwr.error}");
                        }
                    }
                }
            }
        }
    }

    public IEnumerator Co_StartMusicAfterDelay(float delay)
    {
        // Use inspector-assigned music clips
        if (!_ctrl.musicSource || _ctrl.activeMusicClipsFallback == null || _ctrl.activeMusicClipsFallback.Length == 0) 
            yield break;
        
        yield return new WaitForSeconds(delay);
        
        // Pick random clip from inspector array
        var randomClip = _ctrl.activeMusicClipsFallback[Random.Range(0, _ctrl.activeMusicClipsFallback.Length)];
        if (randomClip == null) yield break;
        
        _ctrl.musicSource.Stop();
        _ctrl.musicSource.clip = randomClip;
        _ctrl.musicSource.time = 0f;
        _ctrl.musicSource.Play();
        
        ControllerMain.LogStep($"Music start: '{randomClip.name}'");
        
        // Sync check
        if (_ctrl.musicSyncWithActiveFade)
        {
            float targetVol = _ctrl.musicVolume;
            float fadeDur = Mathf.Max(0.01f, _ctrl.musicFadeIn);
            float t = 0f;
            _ctrl.musicSource.volume = 0f;
            while (t < fadeDur)
            {
                t += Time.deltaTime;
                _ctrl.musicSource.volume = Mathf.Lerp(0f, targetVol, t / fadeDur);
                yield return null;
            }
            _ctrl.musicSource.volume = targetVol;
        }
        else
        {
            _ctrl.musicSource.volume = _ctrl.musicVolume;
        }
    }

    public IEnumerator RampDownToZero(float duration)
    {
        if (!_ctrl.musicSource) yield break;
        float start = _ctrl.musicSource.volume;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            _ctrl.musicSource.volume = Mathf.Lerp(start, 0f, t / duration);
            yield return null;
        }
        _ctrl.musicSource.Stop();
        _ctrl.musicSource.volume = start; 
    }
}
