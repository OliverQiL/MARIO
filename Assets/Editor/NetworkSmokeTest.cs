using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Headless end-to-end check of NetworkClient + EventResolver against
/// tools/mock_gateway.py, without Play Mode or a device. Builds a throwaway
/// scene (resolver, eye-height camera, a stomp-able TestInteractable at its feet 0.5 m ahead),
/// drives the real components' lifecycle methods from the editor loop, and
/// exits with 0 once the expected ACK outcomes arrive (1 on timeout/mismatch).
///
/// Run the mock gateway first, then (Unity closed):
///   Unity -batchmode -projectPath . -executeMethod NetworkSmokeTest.Run \
///         -gatewayUrl wss://127.0.0.1:8765/ -pin <sha256> [-expect 6,5,2,4,2,6]
/// Default script "stomp,stomp,dup,fireball,low:jump,walk" → hit, spent, duplicate,
/// bare-handed strike with no brick in reach, low confidence, state-only walk.
/// Note: no -quit — the editor loop has to keep running to pump async work.
/// </summary>
public static class NetworkSmokeTest
{
    private const double TimeoutSeconds = 30;

    private static NetworkClient s_Client;
    private static EventResolver s_Resolver;
    private static readonly List<int> s_Gates = new();
    private static int[] s_Expected;
    private static double s_Deadline;

    public static void Run()
    {
        var url = Arg("-gatewayUrl", "wss://127.0.0.1:8765/");
        var pin = Arg("-pin", "");
        s_Expected = Array.ConvertAll(Arg("-expect", "6,5,2,4,2,6").Split(','), int.Parse);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cameraGO = new GameObject("Camera", typeof(Camera));
        cameraGO.transform.position = new Vector3(0f, 1.5f, 0f); // eye height, facing +Z
        var target = new GameObject("StompTarget");
        target.transform.position = new Vector3(0f, 0.15f, 0.5f); // at the player's feet, within stomp range
        var interactable = target.AddComponent<TestInteractable>(); // responds to "stomp" by default
        Invoke(interactable, "OnEnable"); // register (OnEnable doesn't run in edit mode)

        var manager = new GameObject("GameManager");
        var state = manager.AddComponent<GameStateManager>();
        s_Resolver = manager.AddComponent<EventResolver>();
        SetField(s_Resolver, "gameState", state);
        SetField(s_Resolver, "arCamera", cameraGO.GetComponent<Camera>());
        state.BeginStage(); // Gate 1 needs a live stage (normally LevelGenerator.OnStageStarted)

        s_Client = manager.AddComponent<NetworkClient>();
        SetField(s_Client, "eventResolver", s_Resolver);
        SetField(s_Client, "gatewayUrl", url);
        SetField(s_Client, "pinnedCertSha256", pin);

        s_Resolver.OnEventProcessed += (evt, result) => s_Gates.Add(result.Gate);

        Invoke(s_Client, "Awake");
        Invoke(s_Client, "OnEnable");
        Invoke(s_Client, "Start");

        s_Deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
        EditorApplication.update += Tick;
        Debug.Log($"[NetworkSmokeTest] Started against {url}, pin '{pin}', expecting gates {string.Join(",", s_Expected)}.");
    }

    private static void Tick()
    {
        Invoke(s_Client, "Update");

        if (s_Gates.Count >= s_Expected.Length)
        {
            Finish(string.Join(",", s_Gates) == string.Join(",", s_Expected)
                ? null
                : $"gate sequence {string.Join(",", s_Gates)} != expected {string.Join(",", s_Expected)}");
        }
        else if (EditorApplication.timeSinceStartup > s_Deadline)
        {
            Finish($"timeout — state {s_Client.State}, last error '{s_Client.LastError}', gates so far {string.Join(",", s_Gates)}");
        }
    }

    private static void Finish(string failure)
    {
        EditorApplication.update -= Tick;
        var delay = EditorApplication.timeSinceStartup + 1.0; // let the last sends flush
        EditorApplication.CallbackFunction exit = null;
        exit = () =>
        {
            if (EditorApplication.timeSinceStartup < delay) { Invoke(s_Client, "Update"); return; }
            EditorApplication.update -= exit;
            Invoke(s_Client, "OnDisable");
            if (failure == null)
            {
                Debug.Log($"[NetworkSmokeTest] PASS — {s_Client.EventsReceived} events, gates {string.Join(",", s_Gates)}, last processing {s_Client.LastProcessingMs:F2} ms.");
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError($"[NetworkSmokeTest] FAIL — {failure}");
                EditorApplication.Exit(1);
            }
        };
        EditorApplication.update += exit;
    }

    private static string Arg(string name, string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static void SetField(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(target, value);

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(target, null);
}
