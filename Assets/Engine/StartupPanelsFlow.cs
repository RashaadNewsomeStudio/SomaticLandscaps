using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using TMPro; 
using SomaticLandscapes.Async; 

public class StartupPanelsFlow
{
    private readonly ArtworkController _ctrl;
    private Coroutine _standbyDotsRoutine;

    public StartupPanelsFlow(ArtworkController ctrl)
    {
        _ctrl = ctrl;
    }

    public async Task RunAsync(CancellationToken ct, System.Func<CancellationToken, Task> onComplete)
    {
        if (_ctrl.introPanel && _ctrl.introGroup)
        {
            _ctrl.introPanel.SetActive(true);
            if (_ctrl.introText1) _ctrl.introText1.gameObject.SetActive(false);
            if (_ctrl.introText2) _ctrl.introText2.gameObject.SetActive(false);
            if (_ctrl.introText3) _ctrl.introText3.gameObject.SetActive(false);
            if (_ctrl.introText4) _ctrl.introText4.gameObject.SetActive(true);
            _ctrl.introGroup.alpha = 0f;
            
            await _ctrl.FadeOneAsync(_ctrl.introGroup, 0f, 1f, _ctrl.introFadeIn, ct);
            
            if (_ctrl.introText1) { _ctrl.introText1.gameObject.SetActive(true); await AsyncExtensions.WaitForSecondsRealtime(_ctrl.introLineStep, ct); }
            if (_ctrl.introText2) { _ctrl.introText2.gameObject.SetActive(true); await AsyncExtensions.WaitForSecondsRealtime(_ctrl.introLineStep, ct); }
            if (_ctrl.introText3) { _ctrl.introText3.gameObject.SetActive(true); await AsyncExtensions.WaitForSecondsRealtime(_ctrl.introLineStep, ct); }
            if (_ctrl.introText4)  _ctrl.introText4.gameObject.SetActive(true);
            
            await AsyncExtensions.WaitForSecondsRealtime(_ctrl.introHold, ct);
            await _ctrl.FadeOneAsync(_ctrl.introGroup, _ctrl.introGroup.alpha, 0f, _ctrl.introFadeOut, ct);
            _ctrl.introPanel.SetActive(false);
        }

        if (_ctrl.adjustPanel && _ctrl.adjustGroup)
        {
            _ctrl.adjustPanel.SetActive(true);
            _ctrl.adjustGroup.alpha = 0f;
            if (_ctrl.adjustText) _ctrl.adjustText.text = _ctrl.standbyBaseText;
            
            if (_standbyDotsRoutine != null) _ctrl.StopCoroutine(_standbyDotsRoutine);
            _standbyDotsRoutine = _ctrl.StartCoroutine(AnimateStandbyDots(_ctrl.adjustText, _ctrl.standbyBaseText, _ctrl.standbyDotStep));
            
            var imgRt = _ctrl.adjustImage ? _ctrl.adjustImage.rectTransform : null;
            var startScale = Vector3.one * Mathf.Clamp(_ctrl.adjustStartScale, 0.01f, 1f);
            if (imgRt) imgRt.localScale = startScale;
            
            await _ctrl.FadeOneAsync(_ctrl.adjustGroup, 0f, 1f, _ctrl.adjustFadeIn, ct);

            float t = 0f, dur = Mathf.Max(0.01f, _ctrl.adjustScaleDuration);
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / dur);
                if (imgRt) imgRt.localScale = Vector3.LerpUnclamped(startScale, Vector3.one, k);
                await Task.Yield();
            }

            if (_standbyDotsRoutine != null) { _ctrl.StopCoroutine(_standbyDotsRoutine); _standbyDotsRoutine = null; }
            if (_ctrl.adjustText) _ctrl.adjustText.text = _ctrl.standbyBaseText + "...";
            
            await _ctrl.FadeOneAsync(_ctrl.adjustGroup, _ctrl.adjustGroup.alpha, 0f, _ctrl.adjustFadeOut, ct);
            _ctrl.adjustPanel.SetActive(false);
        }

        // Callback to start idle
        if (onComplete != null)
            await onComplete(ct);
    }

    private IEnumerator AnimateStandbyDots(TMP_Text label, string baseWord, float step)
    {
        if (!label) yield break;
        int i = 0; float wait = Mathf.Max(0.05f, step);
        while (true)
        {
            int dots = i % 4;
            label.text = dots == 0 ? baseWord : baseWord + new string('.', dots);
            i++; 
            yield return new WaitForSecondsRealtime(wait);
        }
    }
}
