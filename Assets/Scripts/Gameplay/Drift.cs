using UnityEngine;

/// <summary>Scenery: bobs and sways gently about where it was placed (clouds). Each instance is out of phase with the others.</summary>
public class Drift : MonoBehaviour
{
    [SerializeField] private float sway = 0.08f;
    [SerializeField] private float bob = 0.03f;
    [SerializeField] private float period = 7f;

    private Vector3 _home;
    private float _phase;
    private bool _started;

    private void Update()
    {
        // Home is taken on the first frame, after any CompressLayout and EyeHeightPlacement.
        if (!_started) { _home = transform.localPosition; _phase = Random.value * period; _started = true; }
        float t = (Time.time + _phase) * Mathf.PI * 2f / period;
        transform.localPosition = _home + new Vector3(Mathf.Sin(t) * sway, Mathf.Sin(t * 2f) * bob, 0f);
    }
}
