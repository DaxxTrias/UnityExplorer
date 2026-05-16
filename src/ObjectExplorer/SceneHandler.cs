using UnityEngine.SceneManagement;

#nullable enable

namespace UnityExplorer.ObjectExplorer;

public static class SceneHandler
{
    /// <summary>The currently inspected Scene.</summary>
    public static Scene? SelectedScene
    {
        get => selectedScene;
        internal set
        {
            if (selectedScene.HasValue && selectedScene == value)
            {
                return;
            }
            if (!value.HasValue)
            {
                selectedScene = null;
                return;
            }
            selectedScene = value;
            OnInspectedSceneChanged?.Invoke(selectedScene.Value);
        }
    }
    private static Scene? selectedScene;

    /// <summary>The GameObjects in the currently inspected scene.</summary>
    public static IEnumerable<GameObject> CurrentRootObjects { get; private set; } = new GameObject[0];

    /// <summary>All currently loaded Scenes.</summary>
    public static List<Scene> LoadedScenes { get; private set; } = new();
    //private static HashSet<Scene> previousLoadedScenes;

    /// <summary>The names of all scenes in the build settings, if they could be retrieved.</summary>
    public static List<string> AllSceneNames { get; private set; } = new();

    /// <summary>Invoked when the currently inspected Scene changes. The argument is the new scene.</summary>
    public static event Action<Scene>? OnInspectedSceneChanged;

    /// <summary>Invoked whenever the list of currently loaded Scenes changes. The argument contains all loaded scenes after the change.</summary>
    public static event Action<List<Scene>>? OnLoadedScenesUpdated;

    /// <summary>Generally will be 2, unless DontDestroyExists == false, then this will be 1.</summary>
    internal static int DefaultSceneCount => 1;

    /// <summary>Whether or not we are currently inspecting the "HideAndDontSave" asset scene.</summary>
    public static bool InspectingAssetScene => SelectedScene.HasValue && !SelectedScene.Value.IsValid();

    /// <summary>Whether or not we successfuly retrieved the names of the scenes in the build settings.</summary>
    public static bool WasAbleToGetScenesInBuild { get; private set; }

    /// <summary>Whether or not the "DontDestroyOnLoad" scene exists in this game.</summary>
    public static bool DontDestroyExists { get; private set; }

    private const string dontDestroyName = "DontDestroyOnLoad";
    private static bool loggedRootObjectFallback;

    internal static void Init()
    {
        // Unity 6000.3 may use SceneHandle internally and reject the old int-based
        // GetNameInternal/GetAllScenes paths, so DontDestroyOnLoad is discovered from
        // live GameObjects during Update instead.
        DontDestroyExists = false;

        // Try to get all scenes in the build settings. This may not work.
        try
        {
            Type sceneUtil = ReflectionUtility.GetTypeByName("UnityEngine.SceneManagement.SceneUtility");
            if (sceneUtil == null)
            {
                throw new Exception("This version of Unity does not ship with the 'SceneUtility' class, or it was not unstripped.");
            }

            MethodInfo? method = sceneUtil.GetMethod("GetScenePathByBuildIndex", ReflectionUtility.FLAGS);
            int sceneCount = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < sceneCount; i++)
            {
                string? scenePath = (string?)method?.Invoke(null, [ i ]);
                if (string.IsNullOrEmpty(scenePath))
                {
                    continue;
                }
                AllSceneNames.Add(scenePath!);
            }

            WasAbleToGetScenesInBuild = true;
        }
        catch (Exception ex)
        {
            WasAbleToGetScenesInBuild = false;
            ExplorerCore.LogWarning($"Unable to generate list of all Scenes in the build: {ex}");
        }
    }

    internal static void Update()
    {
        GameObject[] allGameObjects = GetAllGameObjects();
        bool inspectedExists = SelectedScene.HasValue && !SelectedScene.Value.IsValid();

        LoadedScenes.Clear();

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                continue;
            }

            // If we have not yet confirmed inspectedExists, check if this scene is our currently inspected one.
            if (!inspectedExists && SelectedScene.HasValue && ScenesMatch(scene, SelectedScene.Value))
            {
                inspectedExists = true;
            }

            LoadedScenes.Add(scene);
        }

        foreach (GameObject go in allGameObjects)
        {
            Scene scene = go.scene;
            if (!scene.IsValid())
            {
                continue;
            }

            if (scene.name == dontDestroyName)
            {
                DontDestroyExists = true;
            }

            if (!ContainsScene(LoadedScenes, scene))
            {
                LoadedScenes.Add(scene);
            }

            if (!inspectedExists && SelectedScene.HasValue && ScenesMatch(scene, SelectedScene.Value))
            {
                inspectedExists = true;
            }
        }

        if (!LoadedScenes.Any(scene => scene.name == dontDestroyName))
        {
            DontDestroyExists = false;
        }

        if (allGameObjects.Any(go => !go.scene.IsValid()))
        {
            LoadedScenes.Add(default);
            if (!inspectedExists && SelectedScene.HasValue && !SelectedScene.Value.IsValid())
            {
                inspectedExists = true;
            }
        }

        // Default to first scene if none selected or previous selection no longer exists.
        if (!inspectedExists)
        {
            SelectedScene = LoadedScenes.Count > 0 ? LoadedScenes[0] : null;
        }

        // Notify on the list changing at all
        OnLoadedScenesUpdated?.Invoke(LoadedScenes);

        // Finally, update the root objects list.
        if (SelectedScene.HasValue && SelectedScene.Value.IsValid())
        {
            CurrentRootObjects = FindRootObjectsInScene(SelectedScene.Value, allGameObjects);
        }
        else
        {
            CurrentRootObjects = FindRootObjectsInAssetScene(allGameObjects);
        }
    }

    private static IEnumerable<GameObject> FindRootObjectsInScene(Scene scene, IEnumerable<GameObject> gameObjects)
    {
        try
        {
            return gameObjects
                .Where(go => go.transform != null
                    && go.transform.parent == null
                    && ScenesMatch(go.scene, scene))
                .ToArray();
        }
        catch (Exception ex)
        {
            ExplorerCore.LogWarning($"Unable to scan root objects for scene '{scene.name}': {ex}");
            return [];
        }
    }

    private static IEnumerable<GameObject> FindRootObjectsInAssetScene(IEnumerable<GameObject> gameObjects)
    {
        try
        {
            return gameObjects
                .Where(go => go.transform != null
                    && go.transform.parent == null
                    && !go.scene.IsValid())
                .ToArray();
        }
        catch (Exception ex)
        {
            ExplorerCore.LogWarning($"Unable to scan root objects for asset scene: {ex}");
            return [];
        }
    }

    private static GameObject[] GetAllGameObjects()
    {
        try
        {
            return EnumerateGameObjects().ToArray();
        }
        catch (Exception ex)
        {
            if (!loggedRootObjectFallback)
            {
                loggedRootObjectFallback = true;
                ExplorerCore.LogWarning($"Unable to scan GameObjects for scene discovery: {ex}");
            }

            return [];
        }
    }

    private static bool ContainsScene(IEnumerable<Scene> scenes, Scene scene)
    {
        return scenes.Any(existing => ScenesMatch(existing, scene));
    }

    private static bool ScenesMatch(Scene a, Scene b)
    {
        bool aValid = a.IsValid();
        bool bValid = b.IsValid();
        if (aValid != bValid)
        {
            return false;
        }
        if (!aValid)
        {
            return true;
        }

        string aPath = a.path;
        string bPath = b.path;
        if (!string.IsNullOrEmpty(aPath) || !string.IsNullOrEmpty(bPath))
        {
            return string.Equals(aPath, bPath, StringComparison.Ordinal);
        }

        return a.buildIndex == b.buildIndex
            && string.Equals(a.name, b.name, StringComparison.Ordinal);
    }

    private static IEnumerable<GameObject> EnumerateGameObjects()
    {
        foreach (UnityEngine.Object obj in RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject)))
        {
            GameObject? go = obj.TryCast<GameObject>();
            if (go != null)
                yield return go;
        }
    }
}
