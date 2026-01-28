using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public class ActiveTriggerButton : MonoBehaviour
{
    private Button _btn;
    private ArtworkController _ctrl;

    void Awake()
    {
        _btn = GetComponent<Button>();
        _ctrl = FindFirstObjectByType<ArtworkController>();

        if (!_ctrl)
        {
            Debug.LogError("[ActiveTriggerButton] ArtworkController not found in scene!");
            _btn.interactable = false;
        }
    }

    void OnEnable()
    {
        if (_btn) _btn.onClick.AddListener(OnClicked);
    }

    void OnDisable()
    {
        if (_btn) _btn.onClick.RemoveListener(OnClicked);
    }

    public void SetInteractable(bool state)
    {
        if (_btn) _btn.interactable = state;
    }

    private void OnClicked()
    {
        if (!_ctrl) return;

        // Log for verification
        //Debug.Log("[ActiveTriggerButton] Clicked! Requesting Active Mode...");
        ControllerMain.LogInfo("[ActiveTriggerButton] Clicked! Processing...");

        // FIX: Active Click Cancels To Ambient (Full JSON Config Support)
        if (_ctrl.activeRunning && _ctrl.activeClickCancelsToAmbient)
        {
             ControllerMain.LogInfo("[ActiveTriggerButton] Clicked while active + cancel rule => returning to IDLE.");
             _ctrl.SetActive(false); // Request return to idle safely
        }
        else
        {
             ControllerMain.LogInfo("[ActiveTriggerButton] Requesting Active Mode...");
             _ctrl.SetActive(true);
        }
    }
}
