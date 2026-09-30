using System;
using UnityEngine;

/// <summary>
/// Bullet Bill cannon (Docs/GAME_DESIGN.md, pass 2): a tower on the stage whose
/// head sits at the player's head height and keeps turning to face them.
/// Up to maxShots per stage, one at a time: it warns (the muzzle glows, and
/// the HUD shows a warning via OnWarning), then fires a BulletBill along the
/// barrel. The Bill flies straight from then on. It holds fire while the
/// player is too close to have time to react. Paused while the session isn't ready.
///
/// Tick(dt) drives it so headless tests can too.
/// </summary>
public class BulletBillCannon : MonoBehaviour
{
    [SerializeField] private BulletBill bulletBillPrefab;
    [Tooltip("Turns (yaw) to face the player; its muzzle is at head height.")]
    [SerializeField] private Transform head;
    [Tooltip("Where Bills leave the barrel (a child of the head).")]
    [SerializeField] private Transform muzzle;
    [Tooltip("Scaled so the column reaches the head (a unit-height cylinder, pivot centred).")]
    [SerializeField] private Transform column;
    [Tooltip("Shown inside the bore during the warning, pulsing gently.")]
    [SerializeField] private GameObject warningGlow;

    [SerializeField] private int maxShots = 2;
    [SerializeField] private float initialDelay = 6f;
    [SerializeField] private float interval = 10f;
    [SerializeField] private float warnSeconds = 1.5f;
    [SerializeField] private float turnDegreesPerSecond = 120f;
    [Tooltip("Don't start a shot while the player is closer than this (horizontal metres).")]
    [SerializeField] private float minPlayerDistance = 1.2f;
    [Tooltip("Head height below the player's eyes, set when the stage appears.")]
    [SerializeField] private float belowEyes = 0.1f;
    [SerializeField] private float minHeadHeight = 0.6f;

    /// <summary>Raised when a cannon starts its warning (for the HUD).</summary>
    public static event Action OnWarning;

    public bool IsWarning => _warnLeft > 0f;
    public int Fired => _fired;

    private BulletBill _current;
    private int _fired;
    private float _timer;
    private float _warnLeft;
    private Vector3 _glowScale = Vector3.one;

    private void Awake()
    {
        if (warningGlow != null) _glowScale = warningGlow.transform.localScale;
    }

    private void Start()
    {
        if (warningGlow != null) warningGlow.SetActive(false);
        var cam = Camera.main;
        if (cam == null || head == null) return;

        float floorY = transform.position.y;
        float y = Mathf.Max(cam.transform.position.y - belowEyes, floorY + minHeadHeight);
        head.position = new Vector3(head.position.x, y, head.position.z);
        if (column != null)
        {
            float h = y - floorY;
            column.localScale = new Vector3(column.localScale.x, h / 2f, column.localScale.z); // Unity cylinders are 2 units tall
            column.position = new Vector3(column.position.x, floorY + h / 2f, column.position.z);
        }
    }

    private void Update()
    {
        var cam = Camera.main;
        if (cam != null) Tick(Time.deltaTime, cam.transform.position);
    }

    /// <summary>Returns the Bill fired this tick, if any.</summary>
    public BulletBill Tick(float dt, Vector3 playerEyes)
    {
        var gs = GameStateManager.Instance;
        if (gs == null || !gs.IsSessionReady || head == null) return null;

        FacePlayer(dt, playerEyes);

        if (_warnLeft > 0f)
        {
            _warnLeft -= dt;
            // A gentle pulse around its authored size (it sits inside the bore).
            if (warningGlow != null) warningGlow.transform.localScale = _glowScale * (1f + 0.12f * Mathf.Sin(_warnLeft * 10f));
            return _warnLeft > 0f ? null : Fire();
        }

        if (bulletBillPrefab == null || _current != null || _fired >= maxShots) return null;
        _timer += dt;
        if (_timer < (_fired == 0 ? initialDelay : interval)) return null;

        var toPlayer = playerEyes - head.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude < minPlayerDistance) return null; // too close to react; wait

        _timer = 0f;
        _warnLeft = warnSeconds;
        if (warningGlow != null) warningGlow.SetActive(true);
        Debug.Log($"[BulletBillCannon] Warning ({_fired + 1}/{maxShots}).");
        OnWarning?.Invoke();
        return null;
    }

    private void FacePlayer(float dt, Vector3 playerEyes)
    {
        var toPlayer = playerEyes - head.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude < 1e-4f) return;
        var target = Quaternion.LookRotation(toPlayer, Vector3.up);
        head.rotation = Quaternion.RotateTowards(head.rotation, target, turnDegreesPerSecond * dt);
    }

    private BulletBill Fire()
    {
        _warnLeft = 0f;
        if (warningGlow != null) warningGlow.SetActive(false);
        _fired++;
        var from = muzzle != null ? muzzle : head;
        _current = Instantiate(bulletBillPrefab, from.position, head.rotation, transform.parent);
        _current.Launch(head.forward);
        Debug.Log($"[BulletBillCannon] Fired ({_fired}/{maxShots}).");
        return _current;
    }
}
