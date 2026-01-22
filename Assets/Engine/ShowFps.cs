using UnityEngine;
using TMPro;

[DisallowMultipleComponent]
public class ShowFPS : MonoBehaviour
{
    [Header("UI")]
    [Tooltip("Assign an existing TextMeshProUGUI element in the scene.")]
    public TextMeshProUGUI fpsText;

    [Header("Settings")]
    public KeyCode toggleKey = KeyCode.F4;
    [Tooltip("How often (in seconds) to update the FPS text.")]
    public float refreshInterval = 0.2f;

    private float _timer;
    private bool _visible = true;

    void Start()
    {
        if (fpsText == null)
        {
            Debug.LogWarning("[ShowFPS] No TextMeshProUGUI assigned! Please assign one in the Inspector.");
            enabled = false;
            return;
        }
        fpsText.gameObject.SetActive(_visible);
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            _visible = !_visible;
            fpsText.gameObject.SetActive(_visible);
        }

        if (!_visible) return;

        _timer += Time.unscaledDeltaTime;
        if (_timer >= refreshInterval)
        {
            float fps = 1f / Time.unscaledDeltaTime;
            fpsText.text = $"{fps:0.} FPS";
            _timer = 0f;
        }
    }
}
