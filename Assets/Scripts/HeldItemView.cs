using UnityEngine;

/// <summary>
/// Shows what the player is carrying in the bottom corner of their view: a
/// child of the AR camera holding a small model of the item (a Koopa shell),
/// shown while GameStateManager.Held says so. It bobs as if carried and pops
/// in when picked up. Works in headset mode too (it's in the 3D view).
/// </summary>
public class HeldItemView : MonoBehaviour
{
    [SerializeField] private GameObject shellModel;
    [SerializeField] private float bob = 0.008f;
    [SerializeField] private float popSeconds = 0.2f;

    private Vector3 _home;
    private Vector3 _scale;
    private float _shownAt = float.NegativeInfinity;
    private bool _wasShown;

    private void Awake()
    {
        if (shellModel == null) return;
        _home = shellModel.transform.localPosition;
        _scale = shellModel.transform.localScale;
        shellModel.SetActive(false);
    }

    private void Update()
    {
        if (shellModel == null) return;
        var gs = GameStateManager.Instance;
        bool show = gs != null && gs.Held == HeldItem.Shell && !gs.IsFinished;
        if (show && !_wasShown) _shownAt = Time.time;
        _wasShown = show;
        if (shellModel.activeSelf != show) shellModel.SetActive(show);
        if (!show) return;

        float pop = Mathf.Clamp01((Time.time - _shownAt) / popSeconds);
        shellModel.transform.localScale = _scale * Mathf.SmoothStep(0f, 1f, pop);
        shellModel.transform.localPosition = _home + Vector3.up * (Mathf.Sin(Time.time * 6f) * bob);
        shellModel.transform.Rotate(Vector3.up, 40f * Time.deltaTime, Space.Self);
    }
}
